(function (root) {
  "use strict";

  var KNOWN_MODELS = [
    "hydrogen-4",
    "gemini-4-hydrogen-high",
    "gemini-4-hydrogen-xhigh",
    "gemini-4-hydrogen-medium",
    "gemini-4-hydrogen-low",
    "gemini-4-argon-high",
    "gemini-3.8-flash-high",
    "claude-opus-5-5-high",
    "claude-sonnet-5-5-high",
    "gemini-3.1-pro-preview",
    "gemini-2.5-flash",
    "gemini-2.5-flash-lite",
    "gemma-4"
  ];

  var REGISTERED_PROJECTS = [
    "HordePunkMaidens",
    "Quantum-Quartz",
    "NexusDaemon"
  ];

  var ALLOWED_MODEL_TARGETS = new Set([
    "/model",
    "/router-model",
    "/worker-model"
  ]);

  function normalizeModelInput(raw) {
    if (typeof raw !== "string") {
      return "";
    }
    return raw.trim().replace(/\s+/g, " ");
  }

  function buildModelCommand(target, presetModel, customModel) {
    var safeTarget = ALLOWED_MODEL_TARGETS.has(target) ? target : "/model";
    var custom = normalizeModelInput(customModel);
    var preset = normalizeModelInput(presetModel);
    var chosen = custom || preset;
    if (!chosen) {
      return null;
    }
    return safeTarget + " " + chosen;
  }

  function buildProjectPrompt(projectName) {
    var clean = normalizeModelInput(projectName);
    if (!clean) {
      return "";
    }
    return "Check status for project " + clean;
  }

  function formatUserDisplay(tg) {
    var user = tg && tg.initDataUnsafe && tg.initDataUnsafe.user;
    if (user) {
      var parts = [];
      if (user.first_name) {
        parts.push(String(user.first_name));
      }
      if (user.username) {
        parts.push("(@" + String(user.username) + ")");
      }
      return parts.length > 0 ? parts.join(" ") : "Telegram user #" + String(user.id || "unknown");
    }
    if (canUseSendData(tg)) {
      return "Keyboard WebApp (sendData active)";
    }
    if (isTelegramRuntime(tg)) {
      return "Telegram session";
    }
    return "Browser preview";
  }

  function applyTelegramTheme(tg, doc) {
    if (!tg || !doc || !doc.documentElement) {
      return;
    }
    if (
      (tg.colorScheme === "light" || tg.colorScheme === "dark") &&
      typeof doc.documentElement.setAttribute === "function"
    ) {
      doc.documentElement.setAttribute("data-color-scheme", tg.colorScheme);
    }
    if (!tg.themeParams || !doc.documentElement.style) {
      return;
    }
    var rootStyle = doc.documentElement.style;
    var map = {
      bg_color: "--tg-theme-bg-color",
      secondary_bg_color: "--tg-theme-secondary-bg-color",
      text_color: "--tg-theme-text-color",
      hint_color: "--tg-theme-hint-color",
      link_color: "--tg-theme-link-color",
      button_color: "--tg-theme-button-color",
      button_text_color: "--tg-theme-button-text-color"
    };

    Object.keys(map).forEach(function (key) {
      var val = tg.themeParams[key];
      if (typeof val === "string" && /^#[0-9a-fA-F]{3,8}$/.test(val.trim())) {
        rootStyle.setProperty(map[key], val.trim());
      }
    });
  }

  function getUtf8ByteLength(text) {
    if (typeof TextEncoder !== "undefined") {
      return new TextEncoder().encode(text).length;
    }
    return unescape(encodeURIComponent(text)).length;
  }

  function createWebAppPayload(commandText, source) {
    return JSON.stringify({
      type: "nexus_command",
      command: commandText,
      source: source || "webapp",
      sentAtUtc: new Date().toISOString()
    });
  }

  function isTelegramRuntime(tg) {
    return Boolean(
      tg &&
      ((typeof tg.initData === "string" && tg.initData.length > 0) ||
        (typeof tg.platform === "string" && tg.platform.length > 0 && tg.platform !== "unknown"))
    );
  }

  function canUseSendData(tg) {
    // Telegram only delivers WebApp.sendData for Reply Keyboard web_app buttons,
    // where platform !== "unknown" and initData is empty. Menu/inline launches set initData and drop sendData.
    return Boolean(
      tg &&
      typeof tg.sendData === "function" &&
      typeof tg.platform === "string" &&
      tg.platform.length > 0 &&
      tg.platform !== "unknown" &&
      (!tg.initData || tg.initData.length === 0)
    );
  }

  async function copyWithFallback(text, clipboard, doc) {
    if (clipboard && typeof clipboard.writeText === "function") {
      try {
        await clipboard.writeText(text);
        return true;
      } catch (err) {
        // Fall through to execCommand fallback if available.
      }
    }

    if (doc && typeof doc.createElement === "function" && doc.body && typeof doc.execCommand === "function") {
      var temp = doc.createElement("textarea");
      temp.value = text;
      temp.setAttribute("readonly", "");
      temp.style.position = "fixed";
      temp.style.opacity = "0";
      temp.style.pointerEvents = "none";
      doc.body.appendChild(temp);
      temp.select();
      try {
        return Boolean(doc.execCommand("copy"));
      } catch (err) {
        return false;
      } finally {
        doc.body.removeChild(temp);
      }
    }

    return false;
  }

  async function dispatchCommand(commandText, options) {
    var trimmed = typeof commandText === "string" ? commandText.trim() : "";
    if (!trimmed) {
      return { ok: false, mode: "empty", message: "Enter a command or prompt first." };
    }

    var tg = options && options.tg;
    var clipboard = options && options.clipboard;
    var doc = options && options.doc;

    if (canUseSendData(tg)) {
      var payload = createWebAppPayload(trimmed, options && options.source);
      if (getUtf8ByteLength(payload) > 4096) {
        return {
          ok: false,
          mode: "too-large",
          message: "Command exceeds Telegram's 4096-byte sendData limit. Shorten it or use Copy."
        };
      }
      try {
        tg.sendData(payload);
        if (tg.HapticFeedback && typeof tg.HapticFeedback.notificationOccurred === "function") {
          tg.HapticFeedback.notificationOccurred("success");
        }
        return {
          ok: true,
          mode: "telegram-sendData",
          command: trimmed,
          message: "Sent via Telegram WebApp.sendData"
        };
      } catch (err) {
        // Fall back to clipboard if WebView rejects sendData.
      }
    }

    var copied = await copyWithFallback(trimmed, clipboard, doc);
    if (copied) {
      return {
        ok: true,
        mode: "clipboard",
        command: trimmed,
        message: isTelegramRuntime(tg)
          ? "Copied command to clipboard (Menu/Inline mode — paste in chat)"
          : "Copied command to clipboard"
      };
    }

    return {
      ok: true,
      mode: "preview",
      command: trimmed,
      message: isTelegramRuntime(tg)
        ? "Prepared command (clipboard blocked — copy from input)"
        : "Prepared command in preview mode"
    };
  }

  function initBrowserApp() {
    if (typeof document === "undefined") {
      return;
    }

    var tg = root.Telegram && root.Telegram.WebApp ? root.Telegram.WebApp : null;
    var envBadge = document.getElementById("env-badge");
    var userLabel = document.getElementById("user-label");
    var promptInput = document.getElementById("prompt-input");
    var sendPromptBtn = document.getElementById("send-prompt-btn");
    var copyPromptBtn = document.getElementById("copy-prompt-btn");
    var clearInputBtn = document.getElementById("clear-input-btn");
    var modelTarget = document.getElementById("model-target");
    var modelPreset = document.getElementById("model-preset");
    var modelCustom = document.getElementById("model-custom");
    var applyModelBtn = document.getElementById("apply-model-btn");
    var modelPreview = document.getElementById("model-preview");
    var projectPills = document.getElementById("project-pills");
    var statusBanner = document.getElementById("status-banner");
    var activityLog = document.getElementById("activity-log");

    modelPreset.replaceChildren();
    KNOWN_MODELS.forEach(function (modelId) {
      var option = document.createElement("option");
      option.value = modelId;
      option.textContent = modelId;
      modelPreset.appendChild(option);
    });

    projectPills.replaceChildren();
    REGISTERED_PROJECTS.forEach(function (projectName) {
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "pill";
      btn.textContent = projectName;
      btn.addEventListener("click", function () {
        promptInput.value = buildProjectPrompt(projectName);
        syncMainButton();
        promptInput.focus();
      });
      projectPills.appendChild(btn);
    });

    function updateModelPreview() {
      var cmd = buildModelCommand(modelTarget.value, modelPreset.value, modelCustom.value);
      modelPreview.textContent = cmd || "Enter a model ID";
    }

    function appendLog(commandText, metaText) {
      var emptyRow = activityLog.querySelector(".log-empty");
      if (emptyRow) {
        emptyRow.remove();
      }
      var li = document.createElement("li");
      var cmdSpan = document.createElement("span");
      cmdSpan.textContent = commandText;
      var metaSpan = document.createElement("span");
      metaSpan.className = "log-meta";
      metaSpan.textContent = metaText;
      li.appendChild(cmdSpan);
      li.appendChild(metaSpan);
      activityLog.prepend(li);

      while (activityLog.children.length > 8) {
        activityLog.removeChild(activityLog.lastElementChild);
      }
    }

    function setStatus(msg) {
      statusBanner.textContent = msg;
    }

    function triggerHaptic() {
      if (tg && tg.HapticFeedback && typeof tg.HapticFeedback.impactOccurred === "function") {
        tg.HapticFeedback.impactOccurred("light");
      }
    }

    async function handleDispatch(commandText, source) {
      triggerHaptic();
      var res = await dispatchCommand(commandText, {
        tg: tg,
        clipboard: root.navigator && root.navigator.clipboard,
        doc: document,
        source: source
      });
      setStatus(res.message);
      if (res.ok && res.command) {
        if (res.mode === "preview" && source !== "prompt_composer") {
          promptInput.value = res.command;
          syncMainButton();
        }
        appendLog(res.command, res.mode);
      }
    }

    function syncMainButton() {
      if (!tg || !tg.MainButton) {
        return;
      }
      if (canUseSendData(tg) && promptInput.value.trim().length > 0) {
        tg.MainButton.setText("Send to Nexus");
        tg.MainButton.show();
      } else {
        tg.MainButton.hide();
      }
    }

    if (isTelegramRuntime(tg)) {
      try {
        tg.ready();
        tg.expand();
      } catch (err) {
        // Ignore SDK lifecycle errors outside real Telegram WebView.
      }
      applyTelegramTheme(tg, document);
      if (typeof tg.onEvent === "function") {
        tg.onEvent("themeChanged", function () {
          applyTelegramTheme(tg, document);
        });
      }
      if (tg.MainButton && typeof tg.MainButton.onClick === "function") {
        tg.MainButton.onClick(function () {
          handleDispatch(promptInput.value, "main_button");
        });
      }
      envBadge.textContent = "Telegram · " + String(tg.platform || "mini-app");
      envBadge.className = "badge badge-ok";
      userLabel.textContent = formatUserDisplay(tg);
    } else {
      envBadge.textContent = "Browser Preview";
      envBadge.className = "badge badge-muted";
      userLabel.textContent = "Standalone mode";
    }

    promptInput.addEventListener("input", syncMainButton);

    sendPromptBtn.addEventListener("click", function () {
      handleDispatch(promptInput.value, "prompt_composer");
    });

    copyPromptBtn.addEventListener("click", async function () {
      var text = promptInput.value.trim();
      if (!text) {
        setStatus("Nothing to copy.");
        return;
      }
      var copied = await copyWithFallback(
        text,
        root.navigator && root.navigator.clipboard,
        document
      );
      if (copied) {
        setStatus("Copied text to clipboard.");
        appendLog(text, "copied");
      } else {
        setStatus("Clipboard copy unavailable — select and copy from the text box.");
      }
    });

    clearInputBtn.addEventListener("click", function () {
      promptInput.value = "";
      syncMainButton();
      setStatus("Cleared prompt input.");
    });

    document.querySelectorAll("[data-command]").forEach(function (button) {
      button.addEventListener("click", function () {
        var cmd = button.getAttribute("data-command") || "";
        handleDispatch(cmd, "quick_button");
      });
    });

    modelTarget.addEventListener("change", updateModelPreview);
    modelPreset.addEventListener("change", updateModelPreview);
    modelCustom.addEventListener("input", updateModelPreview);

    applyModelBtn.addEventListener("click", function () {
      var cmd = buildModelCommand(modelTarget.value, modelPreset.value, modelCustom.value);
      if (!cmd) {
        setStatus("Choose or enter a model ID.");
        return;
      }
      handleDispatch(cmd, "model_switcher");
    });

    updateModelPreview();
    syncMainButton();
  }

  var api = {
    KNOWN_MODELS: KNOWN_MODELS,
    REGISTERED_PROJECTS: REGISTERED_PROJECTS,
    normalizeModelInput: normalizeModelInput,
    buildModelCommand: buildModelCommand,
    buildProjectPrompt: buildProjectPrompt,
    formatUserDisplay: formatUserDisplay,
    applyTelegramTheme: applyTelegramTheme,
    getUtf8ByteLength: getUtf8ByteLength,
    createWebAppPayload: createWebAppPayload,
    isTelegramRuntime: isTelegramRuntime,
    canUseSendData: canUseSendData,
    dispatchCommand: dispatchCommand,
    initBrowserApp: initBrowserApp
  };

  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  } else {
    root.NexusWebApp = api;
    if (typeof document !== "undefined") {
      if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initBrowserApp);
      } else {
        initBrowserApp();
      }
    }
  }
})(typeof window !== "undefined" ? window : globalThis);
