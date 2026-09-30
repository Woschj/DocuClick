/* global DocuClickDocument, VIEWER_TEMPLATE, CYTOSCAPE */
const { Plugin, PluginSettingTab, FileView, Modal, Setting, Notice, TFile, TFolder, AbstractInputSuggest, MarkdownRenderChild, WorkspaceLeaf, normalizePath } = require("obsidian");
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
    if (message.kind === "replace") editor?.replace?.(message.doc);
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

// A CSP additionally prevents imported image links or editor code from
// contacting a network endpoint. Only bundled code and raster data run.
const CSP = `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src 'none'; connect-src 'none'; frame-src 'none'; form-action 'none'">`;

/** Vault path from a property/code block value: `[[Pfad]]`, `[[Pfad|Alias]]` or a plain path. */
function linkTarget(value) {
  const text = String(value ?? "").trim();
  const link = /^\[\[([^\]|#]+)(?:[#|][^\]]*)?\]\]$/.exec(text);
  return (link ? link[1] : text).trim();
}

/**
 * ```docuclick``` code block in any note: another diagram as read-only viewer
 * (same look, zoom, search, image view and guide as the HTML export). First
 * line = the diagram's path or [[link]]. Follows diagram changes live.
 */
class DiagramEmbed extends MarkdownRenderChild {
  constructor(plugin, el, source, sourcePath) { super(el); this.plugin = plugin; this.source = source; this.sourcePath = sourcePath; }
  onload() {
    this.plugin.embeds.add(this);
    const later = () => { clearTimeout(this.timer); this.timer = setTimeout(() => this.render().catch(report), 400); };
    this.registerEvent(this.plugin.app.vault.on("modify", file => { if (file.path === this.file?.path) later(); }));
    this.render().catch(report);
  }
  onunload() { clearTimeout(this.timer); this.plugin.embeds.delete(this); }
  resolve() {
    const { app } = this.plugin;
    const lines = this.source.split("\n").map(line => line.trim()).filter(Boolean);
    const option = lines.find(line => /^(hoehe|höhe|height)\s*:/i.test(line));
    this.height = Math.min(Math.max(parseInt(option?.split(":")[1], 10) || 520, 200), 3000);
    const path = linkTarget(lines.find(line => line !== option));
    if (!path) return null;
    const direct = app.vault.getAbstractFileByPath(normalizePath(path));
    return direct instanceof TFile ? direct : app.metadataCache.getFirstLinkpathDest(path, this.sourcePath);
  }
  async render() {
    const el = this.containerEl, file = this.resolve();
    this.file = file;
    el.empty(); el.addClass("docuclick-embed");
    if (!(file instanceof TFile) || !["md", "docuclick"].includes(file.extension) || file.path === this.sourcePath) {
      el.createDiv({ cls: "docuclick-error", text: "DocuClick: Kein Diagramm gefunden. In den Codeblock den Pfad der Diagramm-Notiz schreiben, z. B. Prozesse/Rechnung stornieren.md" });
      return;
    }
    const header = el.createDiv({ cls: "docuclick-embed-header" });
    header.createSpan({ text: file.basename });
    header.createEl("button", { text: "Im Editor öffnen" }).addEventListener("click", () => this.plugin.openDiagram(file).catch(report));
    let doc;
    try { doc = await this.plugin.loadDocument(await this.plugin.app.vault.read(file)); }
    catch (error) { el.createDiv({ cls: "docuclick-error", text: `DocuClick: ${error.message}` }); return; }
    const frame = el.createEl("iframe", { cls: "docuclick-embed-frame", attr: { sandbox: "allow-scripts allow-modals", title: `Ablauf ${file.basename}` } });
    frame.style.height = `${this.height}px`;
    frame.srcdoc = D.buildHtml(VIEWER_TEMPLATE, CYTOSCAPE, doc, file.basename, { readOnly: true, theme: this.plugin.currentTheme() }).replace("<head>", () => `<head>${CSP}`);
  }
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
class ConfirmDialog extends Modal {
  constructor(app, title, text, items, action, run) { super(app); Object.assign(this, { heading: title, text, items, action, run }); }
  onOpen() {
    this.titleEl.setText(this.heading);
    this.contentEl.createEl("p", { text: this.text });
    const list = this.contentEl.createEl("ul", { cls: "docuclick-confirm-list" });
    for (const item of this.items.slice(0, 50)) list.createEl("li", { text: item });
    if (this.items.length > 50) this.contentEl.createEl("p", { text: `… und ${this.items.length - 50} weitere.` });
    new Setting(this.contentEl)
      .addButton(button => button.setButtonText("Abbrechen").onClick(() => this.close()))
      .addButton(button => button.setButtonText(this.action).setWarning().onClick(() => { this.close(); this.run().catch(report); }));
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
  async onOpen() {
    this.contentEl.addClass("docuclick-view");
    // Diagram notes: switch to the note's text (own notes, step list); old .docuclick files: convert.
    this.noteAction = this.addAction?.("file-text", "Als Notiz anzeigen", () => { if (this.file) this.plugin.toggleView(this.leaf, this.file).catch(report); });
  }
  async onLoadFile(file) {
    this.contentEl.empty();
    this.editorReady = false;
    this.noteAction?.setAttribute?.("aria-label", file.extension === "md" ? "Als Notiz anzeigen" : "In Diagramm-Notiz umwandeln (ersetzt diese .docuclick-Datei)");
    try {
      const base = await this.app.vault.read(file);
      const imagePaths = new Map();
      const doc = await this.plugin.loadDocument(base, imagePaths);
      const state = { file, base, doc, imagePaths, queue: Promise.resolve(), recovery: null, blocked: false, error: null, inbox: [], inboxTimer: null, pendingWrite: null, banner: null, pristineKey: JSON.stringify(doc) };
      this.state = state;
      const frame = this.contentEl.createEl("iframe", { cls: "docuclick-editor", attr: { sandbox: "allow-scripts allow-downloads allow-modals", title: "DocuClick Diagrammeditor" } });
      this.frame = frame;
      const channel = crypto.randomUUID(); this.channel = channel;
      // Exact window + unguessable per-view channel bind messages to this file.
      const onMessage = event => {
        if (event.source !== frame.contentWindow || event.data?.channel !== channel) return;
        const message = event.data;
        if (message.kind === "ready") { this.editorReady = true; return; }
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
      const bridge = `<script>(${frameBridge.toString()})(${D.safeJson(channel)});</script>`;
      frame.srcdoc = D.buildHtml(VIEWER_TEMPLATE, CYTOSCAPE, doc, file.basename, { theme: this.plugin.currentTheme() }).replace("<head>", () => `<head>${CSP}${bridge}`);
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
    // A diagram note whose text part changed (typing in the note view): nothing to reload.
    if (file.extension === "md" && D.noteData(text) === D.noteData(state.base)) { state.base = text; return; }
    let unchanged = false;
    try { unchanged = JSON.stringify(state.doc) === JSON.stringify(await this.plugin.loadDocument(state.base)); } catch { /* keep the warning path */ }
    if (unchanged && !state.blocked) { await this.showExternal(state, text); return; }
    if (state.banner?.isConnected) return;
    const banner = this.contentEl.createDiv({ cls: "docuclick-banner" });
    banner.createSpan({ text: "Die Datei wurde außerhalb dieser Ansicht geändert. Eigene Änderungen werden separat gesichert." });
    banner.createEl("button", { text: "Neu laden" }).addEventListener("click", () => this.reload().catch(report));
    this.contentEl.insertBefore(banner, this.frame);
    state.banner = banner;
  }
  /**
   * Shows a newer file in the open editor without reloading it (zoom and
   * position stay, no flicker while the DocuClick app records). Falls back to
   * a full reload if the editor cannot take it.
   */
  async showExternal(state, text) {
    let doc; const imagePaths = new Map();
    try { doc = await this.plugin.loadDocument(text, imagePaths); }
    catch { await this.reload(); return; }
    if (state !== this.state || !this.frame?.contentWindow || !this.editorReady) { await this.reload(); return; }
    Object.assign(state, { base: text, doc, imagePaths, pristineKey: JSON.stringify(doc), pendingWrite: null, error: null });
    this.post("replace", { doc: { canvas: doc.canvas, flow: doc.flow } });
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
      let next, storage;
      try {
        storage = await this.plugin.storageDoc(state, stored);
        next = this.plugin.fileText(state.file, state.base, storage);
      } catch (error) {
        state.error = error.message; report(error); return;
      }
      if (next.length > D.MAX_BYTES) { state.error = "Diagramm ist zu groß."; report(new Error(state.error)); return; }
      if (next === state.base && !state.blocked) return;
      const isNote = state.file.extension === "md";
      try {
        if (state.blocked) throw new Error("Die Datei wurde außerhalb dieser Ansicht geändert.");
        state.pendingWrite = next;
        await this.app.vault.process(state.file, current => {
          if (current !== state.base) {
            // Only the note's own text changed (edited as text meanwhile): keep it, replace the diagram.
            if (!isNote || D.noteData(current) !== D.noteData(state.base)) { state.blocked = true; throw new Error("Die Datei wurde außerhalb dieser Ansicht geändert."); }
            next = this.plugin.fileText(state.file, current, storage);
            state.pendingWrite = next;
          }
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
    this.embeds = new Set();
    // Leaves where the user chose to see a diagram note as text (leaf -> path).
    this.textLeaves = new WeakMap(); this.textActions = new WeakSet();
    this.registerEvent(this.app.vault.on("modify", file => {
      for (const leaf of this.app.workspace.getLeavesOfType(VIEW_TYPE)) leaf.view?.externalChange?.(file)?.catch(report);
    }));
    this.registerMarkdownCodeBlockProcessor("docuclick", (source, el, ctx) => ctx.addChild(new DiagramEmbed(this, el, source, ctx.sourcePath)));
    // Diagram notes open as diagram tab, like any .docuclick file: a leaf asked to
    // show one as Markdown shows the diagram instead, before anything renders
    // (same approach as the Excalidraw plugin). Tabs switched to text stay text.
    const leafPrototype = WorkspaceLeaf?.prototype, originalSetViewState = leafPrototype?.setViewState, plugin = this;
    if (originalSetViewState) {
      const patched = function (viewState, ...rest) {
        const path = viewState?.state?.file;
        if (viewState?.type === "markdown" && typeof path === "string" && plugin.textLeaves.get(this) !== path && plugin.isDiagramNoteCached(path)) {
          viewState = { ...viewState, type: VIEW_TYPE };
        }
        return originalSetViewState.call(this, viewState, ...rest);
      };
      leafPrototype.setViewState = patched;
      this.register(() => { if (leafPrototype.setViewState === patched) leafPrototype.setViewState = originalSetViewState; });
    }
    // Fallback for notes Obsidian has not indexed yet (just created or synced):
    // switch once the file is opened, or as soon as Obsidian has read its properties.
    this.registerEvent(this.app.workspace.on("file-open", () => this.showDiagramNotes().catch(report)));
    this.registerEvent(this.app.metadataCache.on("changed", file => {
      if (this.isDiagramNoteCached(file?.path)) this.showDiagramNotes().catch(report);
    }));
    this.addCommand({ id: "clean-images", name: "Nicht mehr verwendete Bilder aufräumen", callback: () => this.cleanImages().catch(report) });
    this.app.workspace.onLayoutReady?.(() => this.showDiagramNotes().catch(report));
    this.addCommand({
      id: "toggle-view", name: "Zwischen Diagramm und Notiz umschalten",
      checkCallback: checking => {
        const leaf = this.app.workspace.getMostRecentLeaf?.(), file = leaf?.view?.file;
        if (!(file instanceof TFile) || !["md", "docuclick"].includes(file.extension) || (leaf.view.getViewType() !== VIEW_TYPE && file.extension !== "md")) return false;
        if (!checking) this.toggleView(leaf, file).catch(report);
        return true;
      }
    });
    this.registerExtensions(["docuclick"], VIEW_TYPE);
    this.addRibbonIcon("workflow", "Neues Ablaufdiagramm", () => this.newDiagram());
    this.addCommand({ id: "new-diagram", name: "Neues Ablaufdiagramm", callback: () => this.newDiagram() });
    this.addCommand({ id: "import-html", name: "DocuClick-HTML importieren", callback: () => this.pickHtml() });
    this.registerEvent(this.app.workspace.on("file-menu", (menu, file) => {
      if (file instanceof TFile && file.extension === "docuclick") {
        menu.addItem(item => item.setTitle("In Diagramm-Notiz umwandeln").setIcon("file-text").onClick(() => this.convertToNote(file).catch(report)));
      }
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
    for (const embed of this.embeds) embed.render().catch(report);
  }
  /** Synchronous check from Obsidian's metadata cache (false while not indexed yet). */
  isDiagramNoteCached(path) {
    return typeof path === "string" && path.endsWith(".md") && this.app.metadataCache.getCache?.(path)?.frontmatter?.docuclick === "diagramm";
  }
  /**
   * Screenshots no diagram and no note uses any more (deleted steps). Only
   * image files in folders DocuClick writes to are considered: the app's
   * `Attachments/<Ablauf>/` next to each diagram, the folders of images a
   * diagram uses, and the plugin's image folder.
   */
  async findUnusedImages() {
    const { vault, metadataCache } = this.app;
    const used = new Set(), folders = new Set([cleanFolder(this.settings.imageFolder) || DEFAULT_SETTINGS.imageFolder]);
    const files = vault.getFiles ? vault.getFiles() : [...(vault.getMarkdownFiles?.() ?? [])];
    for (const file of files) {
      const isDiagram = file.extension === "docuclick" || (file.extension === "md" && await this.isDiagramNote(file));
      if (!isDiagram) continue;
      const parent = file.parent?.path && file.parent.path !== "/" ? `${file.parent.path}/` : "";
      folders.add(`${parent}Attachments/${file.basename}`);
      let raw;
      try { const text = await vault.cachedRead(file); raw = JSON.parse(file.extension === "md" ? D.noteData(text) : text); } catch { continue; }
      for (const path of Object.values(raw?.images ?? {})) {
        if (typeof path !== "string") continue;
        used.add(path);
        folders.add(path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "");
      }
    }
    // Images linked or embedded in any note count as used too.
    for (const targets of Object.values(metadataCache.resolvedLinks ?? {})) for (const path of Object.keys(targets)) used.add(path);
    return files.filter(file => IMAGE_TYPES[file.extension?.toLowerCase()] && !used.has(file.path)
      && folders.has(file.parent?.path && file.parent.path !== "/" ? file.parent.path : ""));
  }
  async cleanImages() {
    const unused = await this.findUnusedImages();
    if (!unused.length) { new Notice("DocuClick: Keine ungenutzten Bilder gefunden."); return; }
    new ConfirmDialog(this.app, "Nicht mehr verwendete Bilder",
      `${unused.length} Bild(er) werden von keinem Ablauf und keiner Notiz mehr verwendet und kommen in den Papierkorb:`,
      unused.map(file => file.path), "In den Papierkorb", async () => {
        for (const file of unused) await this.app.fileManager.trashFile(file);
        new Notice(`DocuClick: ${unused.length} Bild(er) in den Papierkorb verschoben.`);
      }).open();
  }
  /** Opens a diagram (note or .docuclick) as diagram tab in a new tab. */
  async openDiagram(file) {
    await this.app.workspace.getLeaf("tab").setViewState({ type: VIEW_TYPE, state: { file: file.path }, active: true });
  }
  /** True for a Markdown note marked as diagram (`docuclick: diagramm`). */
  async isDiagramNote(file) {
    if (!(file instanceof TFile) || file.extension !== "md") return false;
    const value = this.app.metadataCache.getFileCache(file)?.frontmatter?.docuclick;
    if (value !== undefined) return value === "diagramm";
    return D.isDiagramNote(await this.app.vault.cachedRead(file)); // cache not ready yet (new file)
  }
  /** Shows diagram notes opened as Markdown as diagram tab, unless the user switched that tab to text. */
  async showDiagramNotes() {
    for (const leaf of this.app.workspace.getLeavesOfType("markdown")) {
      const file = leaf.view?.file;
      if (!await this.isDiagramNote(file)) continue;
      if (this.textLeaves.get(leaf) === file.path) { this.addDiagramAction(leaf); continue; }
      await leaf.setViewState({ type: VIEW_TYPE, state: { file: file.path } });
    }
  }
  addDiagramAction(leaf) {
    if (this.textActions.has(leaf.view)) return;
    this.textActions.add(leaf.view);
    leaf.view.addAction?.("workflow", "Als Diagramm anzeigen", () => { if (leaf.view?.file) this.toggleView(leaf, leaf.view.file).catch(report); });
  }
  /** Diagram tab <-> note text for a diagram note; an old .docuclick file is converted instead. */
  async toggleView(leaf, file) {
    if (file.extension === "docuclick") { await this.convertToNote(file); return; }
    if (leaf.view?.getViewType() === VIEW_TYPE) {
      this.textLeaves.set(leaf, file.path);
      // Reading mode: the note is for reading; editing text is one click away.
      await leaf.setViewState({ type: "markdown", state: { file: file.path, mode: "preview" } });
      this.addDiagramAction(leaf);
    } else {
      this.textLeaves.delete(leaf);
      await leaf.setViewState({ type: VIEW_TYPE, state: { file: file.path } });
    }
  }
  /** Text of a diagram file: `.docuclick` JSON, or a diagram note (own text of `previous` kept). */
  fileText(file, previous, storage) {
    if (file.extension !== "md") return JSON.stringify(storage, null, 2);
    return D.composeNote(previous, D.noteJson(storage), D.stepsMarkdown(storage));
  }
  /** Turns an old `.docuclick` file into a diagram note next to it; the old file goes to the trash. */
  async convertToNote(file) {
    const text = await this.app.vault.read(file);
    await this.loadDocument(text); // refuse invalid content before creating anything
    const note = await this.createUnique(file.parent?.path, file.basename, "md", this.fileText({ extension: "md" }, null, JSON.parse(text)));
    await this.openDiagram(note);
    await this.app.fileManager.trashFile(file);
    new Notice(`In Diagramm-Notiz umgewandelt: ${note.path}`);
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
      const file = await this.createUnique(folder, name, "md", this.fileText({ extension: "md" }, null, D.emptyDocument()));
      await this.openDiagram(file);
    }).open();
  }
  /**
   * Storage form of a diagram. Screenshots that already are vault files
   * (recorded by the DocuClick apps, or saved earlier) stay files; new ones
   * go to vault files only when that setting is on, else they are embedded.
   */
  async storageDoc(state, doc) {
    const attach = this.settings.imageStorage === "attachments";
    const known = state?.imagePaths ?? new Map();
    if (!attach && !known.size) return doc;
    const folder = cleanFolder(this.settings.imageFolder) || DEFAULT_SETTINGS.imageFolder;
    const used = new Map(), images = {};
    const nodes = [];
    for (const n of doc.flow.nodes) {
      // Keyed by step: two steps may show identical screenshots stored in different files.
      const previous = known.get(n.data.id);
      const path = n.data.imageUrl && (previous?.url === n.data.imageUrl ? previous.path : attach ? await this.saveImage(folder, n.data.imageUrl) : null);
      if (!path) { nodes.push(n); continue; }
      used.set(n.data.id, { url: n.data.imageUrl, path }); images[n.data.id] = path;
      const { imageUrl, ...data } = n.data; nodes.push({ ...n, data });
    }
    if (state) state.imagePaths = used;
    if (!used.size) return doc;
    return { ...doc, flow: { ...doc.flow, nodes }, images };
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
  /**
   * Parses a diagram (`.docuclick` JSON or diagram note) and resolves screenshot files back into
   * embedded images; `paths` (optional Map) receives step id → { url, path }.
   */
  async loadDocument(text, paths) {
    check_(typeof text === "string" && text.length <= D.MAX_BYTES, "Datei ist zu groß.");
    const json = text.trimStart().startsWith("{") ? text : D.noteData(text);
    check_(json, "Diese Notiz enthält keine DocuClick-Diagrammdaten.");
    const raw = JSON.parse(json);
    if (raw?.images && typeof raw.images === "object") {
      const missing = [];
      for (const [nodeId, path] of Object.entries(raw.images)) {
        const node = raw.flow?.nodes?.find?.(n => n?.data?.id === nodeId);
        const clean = typeof path === "string" ? cleanFolder(path) : null;
        const type = clean && IMAGE_TYPES[clean.split(".").pop().toLowerCase()];
        const file = type && this.app.vault.getAbstractFileByPath(clean);
        if (!node?.data || !(file instanceof TFile) || (file.stat?.size ?? 0) > MAX_IMAGE_BYTES) { missing.push(String(path)); continue; }
        node.data.imageUrl = `data:${type};base64,${toBase64(await this.app.vault.readBinary(file))}`;
        paths?.set(nodeId, { url: node.data.imageUrl, path: clean });
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
    const file = await this.createUnique(folder, name, "md", this.fileText({ extension: "md" }, null, await this.storageDoc(null, D.compactForStorage(doc))));
    await this.openDiagram(file);
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
