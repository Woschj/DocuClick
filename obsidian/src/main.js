/* global DocuClickDocument, VIEWER_TEMPLATE, CYTOSCAPE */
const { Plugin, FileView, Modal, Setting, Notice, TFile, normalizePath } = require("obsidian");
const VIEW_TYPE = "docuclick-diagram";
const D = DocuClickDocument;

// Runs only inside a sandboxed, opaque-origin iframe. There is no Obsidian
// API, require(), filesystem access, or parent DOM access in this context.
function frameBridge(channel) {
  let serial = 0, editor;
  const pending = new Map();
  const send = (kind, extra = {}) => parent.postMessage({ channel, kind, ...extra }, "*");
  window.docuclickEditorHost = {
    changed: snapshot => send("change", { snapshot }),
    exportReadOnly: snapshot => send("export-readonly", { snapshot }),
    save: snapshot => new Promise((resolve, reject) => {
      const request = ++serial;
      const timer = setTimeout(() => { pending.delete(request); reject(new Error("Speichern dauert zu lange. Änderungen bleiben in der Ansicht.")); }, 15000);
      pending.set(request, { resolve, reject, timer });
      send("save", { request, snapshot });
    }),
    ready: api => { editor = api; send("ready"); }
  };
  window.addEventListener("message", event => {
    if (event.source !== parent || event.data?.channel !== channel) return;
    const message = event.data;
    if (message.kind === "saved") {
      const entry = pending.get(message.request);
      if (!entry) return;
      clearTimeout(entry.timer); pending.delete(message.request);
      message.error ? entry.reject(new Error(message.error)) : entry.resolve();
    }
    if (message.kind === "flush" && editor) send("flushed", { snapshot: editor.snapshot(), request: message.request });
    if (message.kind === "undo") editor?.undo();
    if (message.kind === "redo") editor?.redo();
  });
}

class NameDialog extends Modal {
  constructor(app, submit) { super(app); this.submit = submit; }
  onOpen() {
    this.titleEl.setText("Neues Ablaufdiagramm");
    let name = "Neuer Ablauf";
    new Setting(this.contentEl).setName("Dateiname").addText(text => {
      text.setValue(name).onChange(value => name = value);
      text.inputEl.addEventListener("keydown", event => { if (event.key === "Enter") accept(); });
      setTimeout(() => { text.inputEl.focus(); text.inputEl.select(); }, 0);
    });
    const accept = () => {
      name = name.trim();
      if (!name || /[\\/:*?"<>|]/.test(name) || name === "." || name === "..") { new Notice("Bitte einen gültigen Dateinamen eingeben."); return; }
      this.close(); this.submit(name).catch(report);
    };
    new Setting(this.contentEl).addButton(button => button.setButtonText("Erstellen").setCta().onClick(accept));
  }
  onClose() { this.contentEl.empty(); }
}
function report(error) { console.error("DocuClick:", error); new Notice(`DocuClick: ${error.message || error}`, 10000); }

class DiagramView extends FileView {
  constructor(leaf, plugin) { super(leaf); this.plugin = plugin; this.state = null; this.flushers = new Map(); }
  getViewType() { return VIEW_TYPE; }
  getDisplayText() { return this.file?.basename || "Ablaufdiagramm"; }
  getIcon() { return "workflow"; }
  async onOpen() { this.contentEl.addClass("docuclick-view"); }
  async onLoadFile(file) {
    this.contentEl.empty();
    try {
      const base = await this.app.vault.read(file);
      const doc = D.parseDocument(base);
      const state = { file, base, doc, queue: Promise.resolve(), recovery: null, blocked: false, error: null };
      this.state = state;
      const frame = this.contentEl.createEl("iframe", { cls: "docuclick-editor", attr: { sandbox: "allow-scripts allow-downloads", title: "DocuClick Diagrammeditor" } });
      this.frame = frame;
      const channel = crypto.randomUUID(); this.channel = channel;
      // Exact window + unguessable per-view channel bind messages to this file.
      const onMessage = event => {
        if (event.source !== frame.contentWindow || event.data?.channel !== channel) return;
        const message = event.data;
        if (!["change", "save", "flushed", "export-readonly"].includes(message.kind)) return;
        let snapshot;
        try {
          const raw = { format: D.FORMAT, version: 1, ...message.snapshot };
          if (JSON.stringify(raw).length > D.MAX_BYTES) throw new Error("Diagramm ist zu groß.");
          snapshot = D.validateDocument(raw);
        } catch (error) {
          if (message.kind === "save") frame.contentWindow?.postMessage({ channel, kind: "saved", request: message.request, error: error.message }, "*");
          if (message.kind === "flushed") this.flushers.get(message.request)?.reject(error);
          report(error); return;
        }
        if (message.kind === "export-readonly") {
          try {
            const html = D.buildHtml(VIEWER_TEMPLATE, CYTOSCAPE, snapshot, file.basename, { readOnly: true });
            this.plugin.createUnique(file.parent?.path, `${file.basename} – Ansicht`, "html", html)
              .then(exported => new Notice(`HTML-Ansicht exportiert: ${exported.path}`)).catch(report);
          } catch (error) { report(error); }
          return;
        }
        state.doc = snapshot;
        const work = this.enqueueSave(state, snapshot);
        if (message.kind === "save") work.then(() => {
          frame.contentWindow?.postMessage({ channel, kind: "saved", request: message.request, error: state.error }, "*");
        });
        if (message.kind === "flushed") work.then(() => this.flushers.get(message.request)?.resolve());
      };
      const ownerWindow = this.contentEl.ownerDocument.defaultView;
      ownerWindow.addEventListener("message", onMessage);
      this.removeListener = () => ownerWindow.removeEventListener("message", onMessage);
      // A CSP additionally prevents imported image links or editor code from
      // contacting a network endpoint. Only bundled code and raster data run.
      const csp = `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src 'none'; connect-src 'none'; frame-src 'none'; form-action 'none'">`;
      const bridge = `<script>(${frameBridge.toString()})(${D.safeJson(channel)});</script>`;
      frame.srcdoc = D.buildHtml(VIEWER_TEMPLATE, CYTOSCAPE, doc, file.basename).replace("<head>", `<head>${csp}${bridge}`);
    } catch (error) {
      this.contentEl.createDiv({ cls: "docuclick-error", text: `Diagramm konnte nicht geöffnet werden. Die Datei wurde nicht verändert.\n\n${error.message}` });
      report(error);
    }
  }
  post(kind, extra = {}) { this.frame?.contentWindow?.postMessage({ channel: this.channel, kind, ...extra }, "*"); }
  enqueueSave(state, snapshot) {
    const next = JSON.stringify(snapshot, null, 2);
    state.queue = state.queue.then(async () => {
      if (next === state.base && !state.blocked) return;
      try {
        if (state.blocked) throw new Error("Die Datei wurde außerhalb dieser Ansicht geändert.");
        await this.app.vault.process(state.file, current => {
          if (current !== state.base) { state.blocked = true; throw new Error("Die Datei wurde außerhalb dieser Ansicht geändert."); }
          return next;
        });
        state.base = next; state.error = null;
      } catch (error) {
        // Preserve local work separately instead of overwriting a newer file.
        state.error = error.message;
        try {
          if (!state.recovery) {
            state.recovery = await this.plugin.createUnique(state.file.parent?.path, `${state.file.basename} – lokale Änderungen`, "docuclick", next);
            new Notice(`Speicherkonflikt/Fehler: Deine Änderungen liegen in ${state.recovery.path}. Original unverändert.`, 15000);
          } else {
            const previous = state.recoveryBase;
            await this.app.vault.process(state.recovery, current => {
              if (current !== previous) throw new Error("Auch die Sicherung wurde extern geändert.");
              return next;
            });
          }
          state.recoveryBase = next;
          state.error += ` Sicherung: ${state.recovery.path}`;
        } catch (backupError) { state.error += ` Sicherung fehlgeschlagen: ${backupError.message}`; report(new Error(state.error)); }
      }
    });
    return state.queue;
  }
  async flush() {
    if (!this.frame?.contentWindow) return;
    const request = crypto.randomUUID();
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.flushers.delete(request); reject(new Error("Editor antwortet nicht. Ansicht bitte geöffnet lassen.")); }, 3000);
      this.flushers.set(request, { resolve: () => { clearTimeout(timer); this.flushers.delete(request); resolve(); }, reject: error => { clearTimeout(timer); this.flushers.delete(request); reject(error); } });
      this.post("flush", { request });
    });
  }
  async onUnloadFile() {
    if (this.frame) {
      try { await this.flush(); } catch (error) { report(error); }
    }
    await this.state?.queue;
    this.removeListener?.(); this.removeListener = null;
    this.frame?.remove(); this.frame = null;
    this.state = null;
  }
  async onClose() { await this.onUnloadFile(); }
}

module.exports = class DocuClickPlugin extends Plugin {
  async onload() {
    this.registerView(VIEW_TYPE, leaf => new DiagramView(leaf, this));
    this.registerExtensions(["docuclick"], VIEW_TYPE);
    this.addRibbonIcon("workflow", "Neues Ablaufdiagramm", () => this.newDiagram());
    this.addCommand({ id: "new-diagram", name: "Neues Ablaufdiagramm", callback: () => this.newDiagram() });
    this.addCommand({ id: "import-html", name: "DocuClick-HTML importieren", callback: () => this.pickHtml() });
    this.registerEvent(this.app.workspace.on("file-menu", (menu, file) => {
      if (file instanceof TFile && file.extension.toLowerCase() === "html") {
        menu.addItem(item => item.setTitle("Als DocuClick-Diagramm importieren").setIcon("workflow").onClick(() => this.importFile(file).catch(report)));
      }
    }));
  }
  newDiagram() {
    new NameDialog(this.app, async name => {
      const folder = this.app.workspace.getActiveFile()?.parent?.path;
      const file = await this.createUnique(folder, name, "docuclick", JSON.stringify(D.emptyDocument(), null, 2));
      await this.app.workspace.getLeaf("tab").openFile(file);
    }).open();
  }
  async createUnique(folder, name, extension, text) {
    const stem = name.replace(/[\\/:*?"<>|]/g, "-").trim() || "Ablauf";
    for (let index = 0; index < 10000; index++) {
      const path = normalizePath(`${folder && folder !== "/" ? folder + "/" : ""}${stem}${index ? ` (${index + 1})` : ""}.${extension}`);
      if (this.app.vault.getAbstractFileByPath(path)) continue;
      try { return await this.app.vault.create(path, text); }
      catch (error) { if (!this.app.vault.getAbstractFileByPath(path)) throw error; }
    }
    throw new Error("Kein freier Dateiname gefunden.");
  }
  async importFile(file) { return this.importText(await this.app.vault.read(file), file.basename, file.parent?.path); }
  async importText(text, name, folder) {
    const doc = D.importHtml(text);
    const file = await this.createUnique(folder, name, "docuclick", JSON.stringify(doc, null, 2));
    await this.app.workspace.getLeaf("tab").openFile(file);
  }
  pickHtml() {
    const input = document.createElement("input");
    input.type = "file"; input.accept = ".html,text/html";
    input.addEventListener("change", async () => {
      const file = input.files?.[0]; if (!file) return;
      try {
        if (file.size > D.MAX_BYTES) throw new Error("Datei ist größer als 64 MB.");
        await this.importText(await file.text(), file.name.replace(/\.html$/i, ""), this.app.workspace.getActiveFile()?.parent?.path);
      } catch (error) { report(error); }
    }, { once: true });
    input.click();
  }
};
