/* global DocuClickDocument, VIEWER_TEMPLATE, CYTOSCAPE */
const { Plugin, PluginSettingTab, FileView, Modal, Setting, Notice, TFile, TFolder, AbstractInputSuggest, normalizePath } = require("obsidian");
const VIEW_TYPE = "docuclick-diagram";
const D = DocuClickDocument;
const DEFAULT_SETTINGS = { themeMode: "docuclick", background: "#1e1e1e", accent: "#7c3aed", themeExport: true, defaultFolder: "", askFolder: true, imageStorage: "embedded", imageFolder: "DocuClick-Bilder" };

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
    if (message.kind === "theme") {
      let style = document.getElementById("docuclick-theme");
      if (!style) { style = document.createElement("style"); style.id = "docuclick-theme"; document.head.appendChild(style); }
      style.textContent = message.css || "";
      window.docuclickRefreshTheme?.();
    }
    if (message.kind === "undo") editor?.undo();
    if (message.kind === "redo") editor?.redo();
  });
}

// Folder field with suggestions from the vault (Obsidian's own suggest popup).
class FolderSuggest extends AbstractInputSuggest {
  constructor(app, inputEl) { super(app, inputEl); this.inputEl = inputEl; }
  getSuggestions(query) {
    const q = query.toLowerCase();
    return this.app.vault.getAllLoadedFiles()
      .filter(file => file instanceof TFolder && file.path.toLowerCase().includes(q))
      .sort((a, b) => a.path.localeCompare(b.path)).slice(0, 50);
  }
  renderSuggestion(folder, el) { el.setText(folder.isRoot?.() || folder.path === "/" ? "/ (Vault-Hauptordner)" : folder.path); }
  selectSuggestion(folder) {
    this.inputEl.value = folder.isRoot?.() || folder.path === "/" ? "" : folder.path;
    this.inputEl.dispatchEvent(new Event("input"));
    this.close();
  }
}

/** Vault-relative folder path from user input: "" = vault root; null = invalid. */
function cleanFolder(value) {
  const trimmed = (value || "").trim().replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
  if (!trimmed) return "";
  if (trimmed.split("/").some(part => !part.trim() || part === "." || part === ".." || /[:*?"<>|]/.test(part))) return null;
  return normalizePath(trimmed);
}

const IMAGE_TYPES = { png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg", webp: "image/webp", gif: "image/gif", bmp: "image/bmp" };
const MAX_IMAGE_BYTES = 32 * 1024 * 1024;
function toBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}
function fromBase64(text) { return Uint8Array.from(atob(text), c => c.charCodeAt(0)); }

class NameDialog extends Modal {
  constructor(app, { folder, askFolder }, submit) { super(app); this.folder = folder; this.askFolder = askFolder; this.submit = submit; }
  onOpen() {
    this.titleEl.setText("Neues Ablaufdiagramm");
    let name = "Neuer Ablauf", folder = this.folder;
    const enter = input => input.addEventListener("keydown", event => { if (event.key === "Enter" && !event.isComposing) accept(); });
    new Setting(this.contentEl).setName("Dateiname").addText(text => {
      text.setValue(name).onChange(value => name = value);
      enter(text.inputEl);
      setTimeout(() => { text.inputEl.focus(); text.inputEl.select(); }, 0);
    });
    if (this.askFolder) {
      new Setting(this.contentEl)
        .setName("Ordner")
        .setDesc("Leer = Vault-Hauptordner. Nicht vorhandene Ordner werden angelegt.")
        .addText(text => {
          text.setPlaceholder("z. B. Prozesse/Buchhaltung").setValue(folder).onChange(value => folder = value);
          new FolderSuggest(this.app, text.inputEl);
        });
    }
    const accept = () => {
      name = name.trim();
      if (!name || /[\\/:*?"<>|]/.test(name) || name === "." || name === "..") { new Notice("Bitte einen gültigen Dateinamen eingeben."); return; }
      const target = cleanFolder(folder);
      if (target === null) { new Notice("Bitte einen gültigen Ordner angeben."); return; }
      this.close(); this.submit(name, target).catch(report);
    };
    new Setting(this.contentEl).addButton(button => button.setButtonText("Erstellen").setCta().onClick(accept));
  }
  onClose() { this.contentEl.empty(); }
}
// Resolves a colour of the active Obsidian theme (CSS variables may be
// built from hsl()/calc()) by letting the browser compute it on a probe.
function vaultColor(property, value) {
  const probe = document.body.createDiv();
  probe.style.display = "none";
  probe.style.setProperty(property, value);
  const computed = getComputedStyle(probe).getPropertyValue(property);
  probe.remove();
  const m = computed.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?/);
  if (!m || m[4] === "0") return null;
  return "#" + [m[1], m[2], m[3]].map(v => Number(v).toString(16).padStart(2, "0")).join("");
}

class DocuClickSettingTab extends PluginSettingTab {
  constructor(app, plugin) { super(app, plugin); this.plugin = plugin; }
  hide() { clearTimeout(this.saveTimer); this.plugin.saveData(this.plugin.settings).catch(report); }
  display() {
    const { containerEl } = this, settings = this.plugin.settings;
    containerEl.empty();
    new Setting(containerEl).setName("Neue Abläufe").setHeading();
    new Setting(containerEl)
      .setName("Standardordner")
      .setDesc("Hier landen neue Abläufe und Importe über den Befehl „DocuClick-HTML importieren“. Leer = Ordner der gerade geöffneten Datei (sonst Vault-Hauptordner). Importe per Rechtsklick auf eine HTML-Datei landen neben dieser Datei.")
      .addText(text => {
        text.setPlaceholder("z. B. Prozesse").setValue(settings.defaultFolder)
          .onChange(async value => {
            const folder = cleanFolder(value);
            text.inputEl.toggleClass?.("docuclick-invalid", folder === null);
            if (folder === null) return;
            settings.defaultFolder = folder;
            clearTimeout(this.saveTimer);
            this.saveTimer = setTimeout(() => this.plugin.saveData(settings).catch(report), 400);
          });
        new FolderSuggest(this.app, text.inputEl);
      });
    new Setting(containerEl)
      .setName("Ordner beim Anlegen abfragen")
      .setDesc("Zeigt im Dialog „Neues Ablaufdiagramm“ ein Ordnerfeld, vorausgefüllt mit dem Standardordner. Aus: direkt im Standardordner anlegen.")
      .addToggle(toggle => toggle.setValue(settings.askFolder)
        .onChange(async value => { settings.askFolder = value; await this.plugin.saveData(settings); }));
    new Setting(containerEl).setName("Screenshots").setHeading();
    new Setting(containerEl)
      .setName("Screenshots speichern")
      .setDesc("„In der Datei“: Alles steckt in einer .docuclick-Datei (einfach zu teilen). „Als Dateien im Vault“: Bilder liegen als eigene Dateien im Bildordner, die Diagrammdatei bleibt klein und schnell. Ältere Plugin-Versionen zeigen dann keine Bilder. Gilt für neu gespeicherte Diagramme.")
      .addDropdown(dropdown => dropdown
        .addOption("embedded", "In der Datei")
        .addOption("attachments", "Als Dateien im Vault")
        .setValue(settings.imageStorage)
        .onChange(async value => { settings.imageStorage = value; await this.plugin.saveData(settings); this.display(); }));
    if (settings.imageStorage === "attachments") {
      new Setting(containerEl)
        .setName("Bildordner")
        .setDesc("Hier werden Screenshots abgelegt (Dateiname = Prüfsumme, gleiche Bilder werden nur einmal gespeichert). Nicht mehr verwendete Bilder werden nicht automatisch gelöscht.")
        .addText(text => {
          text.setPlaceholder("DocuClick-Bilder").setValue(settings.imageFolder)
            .onChange(value => {
              const folder = cleanFolder(value);
              text.inputEl.toggleClass?.("docuclick-invalid", folder === null || folder === "");
              if (!folder) return;
              settings.imageFolder = folder;
              clearTimeout(this.saveTimer);
              this.saveTimer = setTimeout(() => this.plugin.saveData(settings).catch(report), 400);
            });
          new FolderSuggest(this.app, text.inputEl);
        });
    }
    new Setting(containerEl).setName("Darstellung").setHeading();
    new Setting(containerEl)
      .setName("Farbschema")
      .setDesc("Farben des Diagrammeditors. „Obsidian-Theme übernehmen“ folgt automatisch Hell/Dunkel und der Akzentfarbe deines Themes.")
      .addDropdown(dropdown => dropdown
        .addOption("docuclick", "DocuClick (dunkel)")
        .addOption("obsidian", "Obsidian-Theme übernehmen")
        .addOption("custom", "Eigene Farben")
        .setValue(settings.themeMode)
        .onChange(async value => { settings.themeMode = value; await this.plugin.saveSettings(); this.display(); }));
    if (settings.themeMode === "custom") {
      new Setting(containerEl)
        .setName("Hintergrundfarbe")
        .setDesc("Fläche hinter dem Diagramm. Leisten, Menüs und Schrift passen sich an (heller Hintergrund → dunkle Schrift).")
        .addColorPicker(picker => picker.setValue(settings.background)
          .onChange(async value => { settings.background = value; await this.plugin.saveSettings(); }));
      new Setting(containerEl)
        .setName("Detailfarbe")
        .setDesc("Akzent für Schaltflächen, Auswahl, Markierungen und Nummern.")
        .addColorPicker(picker => picker.setValue(settings.accent)
          .onChange(async value => { settings.accent = value; await this.plugin.saveSettings(); }));
      new Setting(containerEl)
        .setName("Aus aktuellem Obsidian-Theme übernehmen")
        .setDesc("Setzt beide Farben einmalig auf die Farben deines Themes; danach frei anpassbar.")
        .addButton(button => button.setButtonText("Übernehmen").onClick(async () => {
          const background = vaultColor("background-color", "var(--background-primary)"), accent = vaultColor("color", "var(--interactive-accent)");
          if (background) settings.background = background;
          if (accent) settings.accent = accent;
          await this.plugin.saveSettings(); this.display();
        }));
    }
    new Setting(containerEl)
      .setName("Farben auch für die HTML-Ansicht")
      .setDesc("Exportierte HTML-Ansichten verwenden dasselbe Farbschema. Aus: immer DocuClick-Standard.")
      .addToggle(toggle => toggle.setValue(settings.themeExport)
        .onChange(async value => { settings.themeExport = value; await this.plugin.saveSettings(); }));
    new Setting(containerEl)
      .addButton(button => button.setButtonText("Auf Standard zurücksetzen").onClick(async () => {
        Object.assign(settings, DEFAULT_SETTINGS); await this.plugin.saveSettings(); this.display();
      }));
  }
}

function check_(condition, message) { if (!condition) throw new Error(message); }
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
      const doc = await this.plugin.loadDocument(base);
      const state = { file, base, doc, queue: Promise.resolve(), recovery: null, blocked: false, error: null, inbox: [], inboxTimer: null, pendingWrite: null, banner: null, pristineKey: JSON.stringify(doc) };
      this.state = state;
      const frame = this.contentEl.createEl("iframe", { cls: "docuclick-editor", attr: { sandbox: "allow-scripts allow-downloads", title: "DocuClick Diagrammeditor" } });
      this.frame = frame;
      const channel = crypto.randomUUID(); this.channel = channel;
      // Exact window + unguessable per-view channel bind messages to this file.
      const onMessage = event => {
        if (event.source !== frame.contentWindow || event.data?.channel !== channel) return;
        const message = event.data;
        if (!["change", "save", "flushed", "export-readonly"].includes(message.kind)) return;
        if (message.kind === "export-readonly") { this.exportReadOnly(file, message.snapshot); return; }
        // The editor sends "change" and "save" for the same edit; process only the
        // newest snapshot once per tick instead of validating/serializing each.
        state.inbox.push(message);
        state.inboxTimer ??= setTimeout(() => this.drainInbox(state, frame, channel), 0);
      };
      const ownerWindow = this.contentEl.ownerDocument.defaultView;
      ownerWindow.addEventListener("message", onMessage);
      this.removeListener = () => ownerWindow.removeEventListener("message", onMessage);
      // A CSP additionally prevents imported image links or editor code from
      // contacting a network endpoint. Only bundled code and raster data run.
      const csp = `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src 'none'; connect-src 'none'; frame-src 'none'; form-action 'none'">`;
      const bridge = `<script>(${frameBridge.toString()})(${D.safeJson(channel)});</script>`;
      frame.srcdoc = D.buildHtml(VIEWER_TEMPLATE, CYTOSCAPE, doc, file.basename, { theme: this.plugin.currentTheme() }).replace("<head>", () => `<head>${csp}${bridge}`);
    } catch (error) {
      this.contentEl.createDiv({ cls: "docuclick-error", text: `Diagramm konnte nicht geöffnet werden. Die Datei wurde nicht verändert.\n\n${error.message}` });
      report(error);
    }
  }
  exportReadOnly(file, raw) {
    try {
      const snapshot = D.validateDocument({ format: D.FORMAT, version: 1, ...raw });
      const theme = this.plugin.settings.themeExport ? this.plugin.currentTheme() : null;
      const html = D.buildHtml(VIEWER_TEMPLATE, CYTOSCAPE, snapshot, file.basename, { readOnly: true, theme });
      this.plugin.createUnique(file.parent?.path, `${file.basename} – Ansicht`, "html", html)
        .then(exported => new Notice(`HTML-Ansicht exportiert: ${exported.path}`)).catch(report);
    } catch (error) { report(error); }
  }
  /** Validates the newest queued snapshot once and answers all queued save/flush requests. */
  drainInbox(state, frame, channel) {
    state.inboxTimer = null;
    const messages = state.inbox.splice(0);
    if (!messages.length || state !== this.state) return;
    const requests = messages.filter(message => message.kind !== "change");
    let stored;
    try {
      const snapshot = D.validateDocument({ format: D.FORMAT, version: 1, ...messages[messages.length - 1].snapshot });
      stored = D.compactForStorage(snapshot);
      state.doc = snapshot;
      // A view that has not changed anything must not rewrite the file (formatting, sync churn).
      if (state.pristineKey) {
        if (JSON.stringify(snapshot) === state.pristineKey) stored = null; else state.pristineKey = null;
      }
    } catch (error) {
      for (const message of requests) {
        if (message.kind === "save") frame.contentWindow?.postMessage({ channel, kind: "saved", request: message.request, error: error.message }, "*");
        if (message.kind === "flushed") this.flushers.get(message.request)?.reject(error);
      }
      report(error); return;
    }
    const work = stored ? this.enqueueSave(state, stored) : state.queue;
    for (const message of requests) {
      if (message.kind === "save") work.then(() => frame.contentWindow?.postMessage({ channel, kind: "saved", request: message.request, error: state.error }, "*"));
      if (message.kind === "flushed") work.then(() => this.flushers.get(message.request)?.resolve());
    }
  }
  /** The vault file changed. Reload silently when there is nothing local to lose, else warn. */
  async externalChange(file) {
    const state = this.state;
    if (!state || file !== state.file || this.reloading) return;
    await state.queue;
    if (state !== this.state) return;
    const text = await this.app.vault.read(file);
    if (text === state.base || text === state.pendingWrite) return;
    let unchanged = false;
    try { unchanged = JSON.stringify(state.doc) === JSON.stringify(await this.plugin.loadDocument(state.base)); } catch { /* keep the warning path */ }
    if (unchanged && !state.blocked) { await this.reload(); return; }
    if (state.banner?.isConnected) return;
    const banner = this.contentEl.createDiv({ cls: "docuclick-banner" });
    banner.createSpan({ text: "Die Datei wurde außerhalb dieser Ansicht geändert. Eigene Änderungen werden separat gesichert." });
    banner.createEl("button", { text: "Neu laden" }).addEventListener("click", () => this.reload().catch(report));
    this.contentEl.insertBefore(banner, this.frame);
    state.banner = banner;
  }
  async reload() {
    const file = this.file;
    if (!file || this.reloading) return;
    this.reloading = true;
    try { await this.state?.queue; this.teardown(); await this.onLoadFile(file); }
    finally { this.reloading = false; }
  }
  teardown() {
    this.removeListener?.(); this.removeListener = null;
    clearTimeout(this.state?.inboxTimer);
    this.frame?.remove(); this.frame = null;
    this.state = null;
  }
  applyTheme(theme) { this.post("theme", { css: D.themeCss(theme) }); }
  post(kind, extra = {}) { this.frame?.contentWindow?.postMessage({ channel: this.channel, kind, ...extra }, "*"); }
  enqueueSave(state, stored) {
    state.queue = state.queue.then(async () => {
      let next;
      try {
        next = await this.plugin.storageText(state, stored);
      } catch (error) {
        state.error = error.message; report(error); return;
      }
      if (next.length > D.MAX_BYTES) { state.error = "Diagramm ist zu groß."; report(new Error(state.error)); return; }
      if (next === state.base && !state.blocked) return;
      try {
        if (state.blocked) throw new Error("Die Datei wurde außerhalb dieser Ansicht geändert.");
        state.pendingWrite = next;
        await this.app.vault.process(state.file, current => {
          if (current !== state.base) { state.blocked = true; throw new Error("Die Datei wurde außerhalb dieser Ansicht geändert."); }
          return next;
        });
        state.base = next; state.error = null;
      } catch (error) {
        state.pendingWrite = null;
        // Preserve local work separately instead of overwriting a newer file.
        state.error = error.message;
        const recoveryText = JSON.stringify(stored, null, 2);
        try {
          if (!state.recovery) {
            state.recovery = await this.plugin.createUnique(state.file.parent?.path, `${state.file.basename} – lokale Änderungen`, "docuclick", JSON.stringify(stored, null, 2));
            new Notice(`Speicherkonflikt/Fehler: Deine Änderungen liegen in ${state.recovery.path}. Original unverändert.`, 15000);
          } else {
            const previous = state.recoveryBase;
            await this.app.vault.process(state.recovery, current => {
              if (current !== previous) throw new Error("Auch die Sicherung wurde extern geändert.");
              return recoveryText;
            });
          }
          state.recoveryBase = recoveryText;
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
    this.teardown();
  }
  async onClose() { await this.onUnloadFile(); }
}

module.exports = class DocuClickPlugin extends Plugin {
  async onload() {
    this.settings = Object.assign({}, DEFAULT_SETTINGS, await this.loadData());
    this.addSettingTab(new DocuClickSettingTab(this.app, this));
    this.registerView(VIEW_TYPE, leaf => new DiagramView(leaf, this));
    // Follow Obsidian theme switches (light/dark, other theme, accent colour).
    this.registerEvent(this.app.workspace.on("css-change", () => { if (this.settings.themeMode === "obsidian") this.refreshThemes(); }));
    this.registerEvent(this.app.vault.on("modify", file => {
      for (const leaf of this.app.workspace.getLeavesOfType(VIEW_TYPE)) leaf.view?.externalChange?.(file)?.catch(report);
    }));
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
  async saveSettings() { await this.saveData(this.settings); this.refreshThemes(); }
  /** null = the editor's own dark look; otherwise { background, accent } as #rrggbb. */
  currentTheme() {
    const { themeMode, background, accent } = this.settings;
    const valid = color => /^#[0-9a-f]{6}$/i.test(color || "");
    if (themeMode === "custom") return valid(background) && valid(accent) ? { background, accent } : null;
    if (themeMode === "obsidian") {
      const fromVault = { background: vaultColor("background-color", "var(--background-primary)"), accent: vaultColor("color", "var(--interactive-accent)") };
      return fromVault.background && fromVault.accent ? fromVault : null;
    }
    return null;
  }
  refreshThemes() {
    const theme = this.currentTheme();
    for (const leaf of this.app.workspace.getLeavesOfType(VIEW_TYPE)) leaf.view?.applyTheme?.(theme);
  }
  /** Default target folder: the configured one, else the active file's folder (vault root without one). */
  targetFolder() {
    const configured = cleanFolder(this.settings.defaultFolder);
    if (configured) return configured;
    const active = this.app.workspace.getActiveFile()?.parent?.path;
    return active && active !== "/" ? active : "";
  }
  async ensureFolder(path) {
    if (!path) return;
    let current = "";
    for (const part of path.split("/")) {
      current = current ? `${current}/${part}` : part;
      const existing = this.app.vault.getAbstractFileByPath(current);
      if (existing instanceof TFolder) continue;
      if (existing) throw new Error(`„${current}“ ist eine Datei, kein Ordner.`);
      try { await this.app.vault.createFolder(current); }
      catch (error) { if (!(this.app.vault.getAbstractFileByPath(current) instanceof TFolder)) throw error; }
    }
  }
  newDiagram() {
    new NameDialog(this.app, { folder: this.targetFolder(), askFolder: this.settings.askFolder }, async (name, folder) => {
      await this.ensureFolder(folder);
      const file = await this.createUnique(folder, name, "docuclick", JSON.stringify(D.emptyDocument(), null, 2));
      await this.app.workspace.getLeaf("tab").openFile(file);
    }).open();
  }
  /** Text of a `.docuclick` file; screenshots go to vault files when that setting is on. */
  async storageText(state, doc) {
    if (this.settings.imageStorage !== "attachments") return JSON.stringify(doc, null, 2);
    const folder = cleanFolder(this.settings.imageFolder) || DEFAULT_SETTINGS.imageFolder;
    const known = state?.imagePaths ?? new Map(), used = new Map(), images = {};
    const nodes = [];
    for (const n of doc.flow.nodes) {
      if (!n.data.imageUrl) { nodes.push(n); continue; }
      const path = known.get(n.data.imageUrl) ?? await this.saveImage(folder, n.data.imageUrl);
      used.set(n.data.imageUrl, path); images[n.data.id] = path;
      const { imageUrl, ...data } = n.data; nodes.push({ ...n, data });
    }
    if (state) state.imagePaths = used;
    return JSON.stringify({ ...doc, flow: { ...doc.flow, nodes }, images }, null, 2);
  }
  async saveImage(folder, dataUri) {
    const match = /^data:image\/(png|jpeg|jpg|webp|gif|bmp);base64,([a-z0-9+/=\s]+)$/i.exec(dataUri);
    if (!match) throw new Error("Unbekanntes Bildformat.");
    const bytes = fromBase64(match[2].replace(/\s/g, ""));
    const hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)), b => b.toString(16).padStart(2, "0")).join("").slice(0, 32);
    const path = normalizePath(`${folder}/${hash}.${match[1].toLowerCase() === "jpeg" ? "jpg" : match[1].toLowerCase()}`);
    if (!(this.app.vault.getAbstractFileByPath(path) instanceof TFile)) {
      await this.ensureFolder(folder);
      try { await this.app.vault.createBinary(path, bytes.buffer); }
      catch (error) { if (!(this.app.vault.getAbstractFileByPath(path) instanceof TFile)) throw error; }
    }
    return path;
  }
  /** Parses a `.docuclick` text and resolves screenshot files back into embedded images. */
  async loadDocument(text) {
    check_(typeof text === "string" && text.length <= D.MAX_BYTES, "Datei ist zu groß.");
    const raw = JSON.parse(text);
    if (raw?.images && typeof raw.images === "object") {
      const missing = [];
      for (const [nodeId, path] of Object.entries(raw.images)) {
        const node = raw.flow?.nodes?.find?.(n => n?.data?.id === nodeId);
        const clean = typeof path === "string" ? cleanFolder(path) : null;
        const type = clean && IMAGE_TYPES[clean.split(".").pop().toLowerCase()];
        const file = type && this.app.vault.getAbstractFileByPath(clean);
        if (!node?.data || !(file instanceof TFile) || (file.stat?.size ?? 0) > MAX_IMAGE_BYTES) { missing.push(String(path)); continue; }
        node.data.imageUrl = `data:${type};base64,${toBase64(await this.app.vault.readBinary(file))}`;
      }
      if (missing.length) new Notice(`DocuClick: ${missing.length} Bilddatei(en) fehlen oder sind ungültig, z. B. ${missing[0]}`, 10000);
    }
    return D.validateDocument(raw);
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
  async importFile(file) {
    if (file.stat?.size > D.MAX_BYTES) throw new Error("Datei ist größer als 64 MB.");
    return this.importText(await this.app.vault.read(file), file.basename, file.parent?.path);
  }
  async importText(text, name, folder) {
    const doc = D.importHtml(text);
    const file = await this.createUnique(folder, name, "docuclick", await this.storageText(null, D.compactForStorage(doc)));
    await this.app.workspace.getLeaf("tab").openFile(file);
  }
  pickHtml() {
    const input = document.createElement("input");
    input.type = "file"; input.accept = ".html,text/html";
    input.addEventListener("change", async () => {
      const file = input.files?.[0]; if (!file) return;
      try {
        if (file.size > D.MAX_BYTES) throw new Error("Datei ist größer als 64 MB.");
        const folder = this.targetFolder();
        await this.ensureFolder(folder);
        await this.importText(await file.text(), file.name.replace(/\.html$/i, ""), folder);
      } catch (error) { report(error); }
    }, { once: true });
    input.click();
  }
};
