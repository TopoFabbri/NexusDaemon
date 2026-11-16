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

test("formatUserDisplay formats Telegram user safely and falls back", () => {
  assert.equal(app.formatUserDisplay(null), "Browser preview");
  assert.equal(
    app.formatUserDisplay({ initDataUnsafe: { user: { first_name: "Topo", username: "topofabbri" } } }),
    "Topo (@topofabbri)"
  );
  assert.equal(
    app.formatUserDisplay({ initDataUnsafe: { user: { id: 42 } } }),
    "Telegram user #42"
  );
});

test("applyTelegramTheme sets only valid hex colors", () => {
  const props = new Map();
  const fakeDoc = {
    documentElement: {
      style: {
        setProperty(k, v) {
          props.set(k, v);
        }
      }
    }
  };
  app.applyTelegramTheme(
    {
      themeParams: {
        bg_color: "#112233",
        text_color: "not-a-color; background: url(evil)",
        button_color: "#3b82f6"
      }
    },
    fakeDoc
  );

  assert.equal(props.get("--tg-theme-bg-color"), "#112233");
  assert.equal(props.get("--tg-theme-button-color"), "#3b82f6");
  assert.equal(props.has("--tg-theme-text-color"), false);
});

test("dispatchCommand uses Telegram.WebApp.sendData when available and falls back cleanly", async () => {
  const emptyRes = await app.dispatchCommand("   ", {});
  assert.equal(emptyRes.ok, false);
  assert.equal(emptyRes.mode, "empty");

  const sentPayloads = [];
  const tg = {
    initData: "query_id=123",
    sendData(data) {
      sentPayloads.push(JSON.parse(data));
    },
    HapticFeedback: { notificationOccurred() {} }
  };
  const tgRes = await app.dispatchCommand("/status", { tg, source: "quick_button" });
  assert.equal(tgRes.ok, true);
  assert.equal(tgRes.mode, "telegram-sendData");
  assert.equal(sentPayloads.length, 1);
  assert.equal(sentPayloads[0].command, "/status");
  assert.equal(sentPayloads[0].source, "quick_button");

  const copied = [];
  const menuModeTg = {
    initData: "query_id=123",
    sendData() {
      throw new Error("WebAppMethodUnsupported");
    }
  };
  const fallbackRes = await app.dispatchCommand("/models", {
    tg: menuModeTg,
    clipboard: {
      async writeText(txt) {
        copied.push(txt);
      }
    }
  });
  assert.equal(fallbackRes.ok, true);
  assert.equal(fallbackRes.mode, "clipboard");
  assert.deepEqual(copied, ["/models"]);
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
