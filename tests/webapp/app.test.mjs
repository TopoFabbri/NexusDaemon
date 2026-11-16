import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const webappDir = path.resolve(import.meta.dirname, "../../webapp");
const app = require(path.join(webappDir, "app.js"));

test("buildModelCommand prefers custom input and validates target", () => {
  assert.equal(app.buildModelCommand("/model", "hydrogen-4", ""), "/model hydrogen-4");
  assert.equal(
    app.buildModelCommand("/router-model", "hydrogen-4", "  gemini-3.1-pro-preview  "),
    "/router-model gemini-3.1-pro-preview"
  );
  assert.equal(
    app.buildModelCommand("/invalid-target", "hydrogen-4", "claude-opus-5-5-high"),
    "/model claude-opus-5-5-high"
  );
  assert.equal(app.buildModelCommand("/model", "", "   "), null);
});

test("buildProjectPrompt handles empty and normal inputs", () => {
  assert.equal(app.buildProjectPrompt("NexusDaemon"), "Check status for project NexusDaemon");
  assert.equal(app.buildProjectPrompt("   "), "");
  assert.equal(app.buildProjectPrompt(null), "");
});

test("formatUserDisplay formats Telegram user, keyboard mode, session, and browser fallback", () => {
  assert.equal(app.formatUserDisplay(null), "Browser preview");
  assert.equal(app.formatUserDisplay({ platform: "unknown", initData: "" }), "Browser preview");
  assert.equal(app.formatUserDisplay({ platform: "android", initData: "", sendData() {} }), "Keyboard WebApp (sendData active)");
  assert.equal(app.formatUserDisplay({ platform: "tdesktop", initData: "query_id=1" }), "Telegram session");
  assert.equal(
    app.formatUserDisplay({ initDataUnsafe: { user: { first_name: "Topo", username: "topofabbri" } } }),
    "Topo (@topofabbri)"
  );
  assert.equal(
    app.formatUserDisplay({ initDataUnsafe: { user: { id: 42 } } }),
    "Telegram user #42"
  );
});

test("applyTelegramTheme sets colorScheme and only valid hex colors", () => {
  const props = new Map();
  const attrs = new Map();
  const fakeDoc = {
    documentElement: {
      setAttribute(k, v) {
        attrs.set(k, v);
      },
      style: {
        setProperty(k, v) {
          props.set(k, v);
        }
      }
    }
  };
  app.applyTelegramTheme(
    {
      colorScheme: "light",
      themeParams: {
        bg_color: "#112233",
        text_color: "not-a-color; background: url(evil)",
        button_color: "#3b82f6"
      }
    },
    fakeDoc
  );

  assert.equal(attrs.get("data-color-scheme"), "light");
  assert.equal(props.get("--tg-theme-bg-color"), "#112233");
  assert.equal(props.get("--tg-theme-button-color"), "#3b82f6");
  assert.equal(props.has("--tg-theme-text-color"), false);
});

test("dispatchCommand respects Telegram Keyboard vs Menu Button sendData rules and byte limit", async () => {
  const emptyRes = await app.dispatchCommand("   ", {});
  assert.equal(emptyRes.ok, false);
  assert.equal(emptyRes.mode, "empty");

  // Browser preview: platform=unknown, initData="" -> never calls sendData
  assert.equal(app.canUseSendData({ platform: "unknown", initData: "", sendData() {} }), false);

  // Keyboard Button launch: platform !== "unknown" and initData === "" -> sendData is allowed
  const sentPayloads = [];
  const keyboardTg = {
    platform: "android",
    initData: "",
    sendData(data) {
      sentPayloads.push(JSON.parse(data));
    },
    HapticFeedback: { notificationOccurred() {} }
  };
  assert.equal(app.canUseSendData(keyboardTg), true);
  const tgRes = await app.dispatchCommand("/status", { tg: keyboardTg, source: "quick_button" });
  assert.equal(tgRes.ok, true);
  assert.equal(tgRes.mode, "telegram-sendData");
  assert.equal(sentPayloads.length, 1);
  assert.equal(sentPayloads[0].command, "/status");
  assert.equal(sentPayloads[0].source, "quick_button");

  // Payload > 4096 UTF-8 bytes in Keyboard Button mode is rejected before calling sendData
  const oversizedRes = await app.dispatchCommand("a".repeat(4050), { tg: keyboardTg, source: "prompt_composer" });
  assert.equal(oversizedRes.ok, false);
  assert.equal(oversizedRes.mode, "too-large");
  assert.equal(sentPayloads.length, 1);

  // Menu Button / Inline launch: initData is non-empty -> sendData is silently dropped by Telegram,
  // so app must NOT call sendData and must fall back to clipboard.
  let menuSendDataCalled = false;
  const copied = [];
  const menuModeTg = {
    platform: "tdesktop",
    initData: "query_id=123",
    sendData() {
      menuSendDataCalled = true;
    }
  };
  assert.equal(app.canUseSendData(menuModeTg), false);
  const fallbackRes = await app.dispatchCommand("/models", {
    tg: menuModeTg,
    clipboard: {
      async writeText(txt) {
        copied.push(txt);
      }
    }
  });
  assert.equal(menuSendDataCalled, false);
  assert.equal(fallbackRes.ok, true);
  assert.equal(fallbackRes.mode, "clipboard");
  assert.deepEqual(copied, ["/models"]);
});

test("initBrowserApp wires DOM elements and handles project pills, model switcher, and quick commands", async () => {
  function makeElement(tag, id) {
    const el = {
      tagName: tag.toUpperCase(),
      id: id || "",
      value: "",
      textContent: "",
      className: "",
      style: {},
      attributes: new Map(),
      listeners: new Map(),
      children: [],
      get lastElementChild() {
        return this.children[this.children.length - 1] || null;
      },
      setAttribute(k, v) {
        this.attributes.set(k, String(v));
      },
      getAttribute(k) {
        return this.attributes.has(k) ? this.attributes.get(k) : null;
      },
      addEventListener(evt, fn) {
        if (!this.listeners.has(evt)) this.listeners.set(evt, []);
        this.listeners.get(evt).push(fn);
      },
      async trigger(evt) {
        const list = this.listeners.get(evt) || [];
        for (const fn of list) {
          await fn({ target: this });
        }
      },
      appendChild(child) {
        this.children.push(child);
      },
      prepend(child) {
        this.children.unshift(child);
      },
      removeChild(child) {
        this.children = this.children.filter((c) => c !== child);
      },
      replaceChildren() {
        this.children = [];
      },
      querySelector(sel) {
        if (sel === ".log-empty") {
          return this.children.find((c) => c.className === "log-empty") || null;
        }
        return null;
      },
      remove() {},
      focus() {}
    };
    return el;
  }

  const elements = {
    "env-badge": makeElement("div", "env-badge"),
    "user-label": makeElement("span", "user-label"),
    "prompt-input": makeElement("textarea", "prompt-input"),
    "send-prompt-btn": makeElement("button", "send-prompt-btn"),
    "copy-prompt-btn": makeElement("button", "copy-prompt-btn"),
    "clear-input-btn": makeElement("button", "clear-input-btn"),
    "model-target": makeElement("select", "model-target"),
    "model-preset": makeElement("select", "model-preset"),
    "model-custom": makeElement("input", "model-custom"),
    "apply-model-btn": makeElement("button", "apply-model-btn"),
    "model-preview": makeElement("code", "model-preview"),
    "project-pills": makeElement("div", "project-pills"),
    "status-banner": makeElement("span", "status-banner"),
    "activity-log": makeElement("ul", "activity-log")
  };
  elements["model-target"].value = "/model";

  const quickBtn = makeElement("button");
  quickBtn.setAttribute("data-command", "/status");

  const origDoc = globalThis.document;
  globalThis.document = {
    documentElement: makeElement("html"),
    getElementById(id) {
      return elements[id] || null;
    },
    createElement(tag) {
      return makeElement(tag);
    },
    querySelectorAll(sel) {
      return sel === "[data-command]" ? [quickBtn] : [];
    }
  };

  try {
    app.initBrowserApp();
    elements["model-preset"].value = "hydrogen-4";
    assert.equal(elements["env-badge"].textContent, "Browser Preview");
    assert.equal(elements["model-preset"].children.length, app.KNOWN_MODELS.length);
    assert.equal(elements["project-pills"].children.length, app.REGISTERED_PROJECTS.length);

    // Click a project pill -> populates prompt input
    await elements["project-pills"].children[2].trigger("click");
    assert.equal(elements["prompt-input"].value, "Check status for project NexusDaemon");

    // Click quick command when clipboard unavailable -> falls back to preview & updates log
    await quickBtn.trigger("click");
    assert.equal(elements["activity-log"].children[0].children[0].textContent, "/status");
    assert.equal(elements["prompt-input"].value, "/status");
  } finally {
    globalThis.document = origDoc;
  }
});

test("webapp static files exist and reference valid relative assets", () => {
  const html = fs.readFileSync(path.join(webappDir, "index.html"), "utf8");
  const css = fs.readFileSync(path.join(webappDir, "styles.css"), "utf8");
  assert.ok(fs.existsSync(path.join(webappDir, ".nojekyll")));
  assert.match(html, /https:\/\/telegram\.org\/js\/telegram-web-app\.js/);
  assert.match(html, /href="\.\/styles\.css"/);
  assert.match(html, /src="\.\/app\.js"/);
  assert.doesNotMatch(html, /\sonclick=/i);
  assert.ok(css.includes("--tg-theme-bg-color"));
});
