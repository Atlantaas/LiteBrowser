/* Pré-remplissage Mobilic dans LiteBrowser (WebView2).
 * Aucun appel API, aucune connexion, le bouton Continuer n'est jamais cliqué.
 * Ne s'active que sur https://mobilic.beta.gouv.fr (cadre principal).
 */
(function () {
  "use strict";
  if (window.__lbMobilicPrefill) return;
  var allowed = location.hostname === "mobilic.beta.gouv.fr" || window.__MOBILIC_PREFILL_TEST === true;
  if (!allowed) return;
  if (window.top !== window) return;
  try { Object.defineProperty(window, "__lbMobilicPrefill", { value: true, enumerable: false }); } catch (e) { window.__lbMobilicPrefill = true; }

  var CSS_TEXT = [
    ".mobilic-prefill-fab, .mobilic-prefill-gear {",
    "  position: fixed; z-index: 2147483646;",
    "  font: 600 14px/1.2 system-ui, \"Segoe UI\", sans-serif;",
    "  color: #fff; background: #0f285a; border: 0; cursor: pointer;",
    "  box-shadow: 0 6px 20px rgba(15, 40, 90, 0.35);",
    "  pointer-events: auto;",
    "}",
    ".mobilic-prefill-fab {",
    "  right: 16px; top: 50%; transform: translateY(-50%);",
    "  border-radius: 999px; padding: 12px 16px;",
    "}",
    ".mobilic-prefill-gear {",
    "  right: 16px; top: calc(50% + 36px); transform: translateY(-50%);",
    "  border-radius: 999px; padding: 8px 12px; font-size: 12px; background: #173a82;",
    "}",
    ".mobilic-prefill-fab:hover, .mobilic-prefill-gear:hover { background: #1b4aaa; }",
    ".mobilic-prefill-fab.is-busy, .mobilic-prefill-fab:disabled { opacity: 0.75; cursor: wait; }",
    ".mobilic-prefill-toast {",
    "  position: fixed; right: 16px; bottom: 16px; z-index: 2147483647;",
    "  max-width: min(420px, calc(100vw - 32px)); padding: 12px 14px; border-radius: 10px;",
    "  font: 14px/1.4 system-ui, \"Segoe UI\", sans-serif; color: #122;",
    "  background: #f4faf4; border: 1px solid #8bc48b; box-shadow: 0 8px 24px rgba(0,0,0,.18);",
    "  pointer-events: none;",
    "}",
    ".mobilic-prefill-toast.is-partial { background: #fff8e8; border-color: #e0b04a; }",
    ".mobilic-prefill-toast.is-out { opacity: 0; transition: opacity .35s ease; }",
    ".mobilic-prefill-backdrop {",
    "  position: fixed; inset: 0; z-index: 2147483645;",
    "  background: transparent; pointer-events: auto;",
    "}",
    ".mobilic-prefill-panel {",
    "  position: fixed; right: 148px; top: 24px; bottom: 24px;",
    "  z-index: 2147483647; width: min(340px, calc(100vw - 180px));",
    "  display: flex; flex-direction: column;",
    "  background: #f6f7fb; color: #122; border-radius: 12px;",
    "  font: 14px/1.4 system-ui, \"Segoe UI\", sans-serif;",
    "  box-shadow: 0 12px 32px rgba(0,0,0,.28); border: 1px solid #c9ceda;",
    "  pointer-events: auto; overflow: visible;",
    "}",
    ".mobilic-prefill-panel header {",
    "  background: #0f285a; color: #fff; padding: 10px 12px; flex: 0 0 auto;",
    "}",
    ".mobilic-prefill-panel header .title-row {",
    "  display: flex; align-items: flex-start; justify-content: space-between; gap: 8px;",
    "}",
    ".mobilic-prefill-panel header h2 { margin: 0 0 2px; font-size: 14px; }",
    ".mobilic-prefill-panel header p { margin: 4px 0 0; font-size: 11px; opacity: .9; }",
    ".mobilic-prefill-panel .icon-close {",
    "  flex: 0 0 auto; width: 28px; height: 28px; padding: 0; margin: 0;",
    "  border: 0; border-radius: 8px; background: rgba(255,255,255,.15);",
    "  color: #fff; font: 700 20px/1 system-ui, \"Segoe UI\", sans-serif; cursor: pointer;",
    "}",
    ".mobilic-prefill-panel .icon-close:hover { background: rgba(255,255,255,.28); }",
    ".mobilic-prefill-panel .body { padding: 10px 12px 12px; overflow: auto; flex: 1 1 auto; min-height: 0; }",
    ".mobilic-prefill-panel label { display: block; font-weight: 600; font-size: 12px; margin: 8px 0 3px; }",
    ".mobilic-prefill-panel input, .mobilic-prefill-panel textarea {",
    "  width: 100%; box-sizing: border-box; padding: 6px 8px; border: 1px solid #c9ceda;",
    "  border-radius: 6px; font: inherit; background: #fff;",
    "}",
    ".mobilic-prefill-panel textarea { min-height: 54px; resize: vertical; }",
    ".mobilic-prefill-panel .row { display: flex; gap: 8px; align-items: end; }",
    ".mobilic-prefill-panel .row > div { flex: 1; }",
    ".mobilic-prefill-panel .hint { font-size: 11px; color: #556; margin: 2px 0 0; }",
    ".mobilic-prefill-panel .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }",
    ".mobilic-prefill-panel button { border: 0; border-radius: 8px; padding: 8px 10px; font: 600 13px system-ui; cursor: pointer; }",
    ".mobilic-prefill-panel .actions button { flex: 1 1 140px; }",
    ".mobilic-prefill-panel button.primary { background: #0f285a; color: #fff; }",
    ".mobilic-prefill-panel button.ghost { background: #e6e9f2; color: #122; }",
    ".mobilic-prefill-panel .status { font-size: 12px; margin-top: 6px; color: #1a5; min-height: 1.2em; }",
  ].join("\n");

/**
 * Logique de pré-remplissage du formulaire « mission passée » Mobilic.
 *
 * Source officielle (MTES-MCT/mobilic) :
 * - web/common/NewMissionForm.js : TextField, MobileDatePicker (@mui/x-date-pickers v5,
 *   locale fr, disableMaskedInput, minDate = J-30, maxDate = aujourd'hui),
 *   AddressField, VehicleFieldForApp, motif (textarea, max 48).
 * - web/common/AddressField.js : Autocomplete MUI, groupBy « Adresses enregistrées »
 *   seulement si le champ est vide (< 3 caractères, sinon recherche Géoplateforme).
 *   Il ne faut PAS taper l'adresse : ouvrir la liste et cliquer l'option.
 *   Les deux lieux partagent id="address-field" : on cible par le libellé.
 * - web/common/VehicleField.js : Autocomplete MUI freeSolo, id="vehicle-booking".
 *
 * Aucun appel réseau, aucune soumission : le bouton « Continuer » n'est jamais cliqué.
 */
(function (root) {
  "use strict";

  var DEFAULTS = {
    missionName: "mondial relay",
    useToday: true,
    missionDate: "",
    startLocation: "Chronopost-Mondial Relay (Rombas)",
    endLocation: "Chronopost-Mondial Relay (Rombas)",
    vehicle: "175",
    reason: "1",
    startTime: "06:00",
  };

  var MONTHS_FR = [
    "janvier",
    "février",
    "mars",
    "avril",
    "mai",
    "juin",
    "juillet",
    "août",
    "septembre",
    "octobre",
    "novembre",
    "décembre",
  ];

  function sleep(ms) {
    return new Promise(function (resolve) {
      setTimeout(resolve, ms);
    });
  }

  function normalize(text) {
    return String(text || "")
      .replace(/\u00a0/g, " ")
      .replace(/\s+/g, " ")
      .replace(/\s*\*\s*$/g, "")
      .trim()
      .toLowerCase()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "");
  }

  function visible(el) {
    if (!el || !el.getBoundingClientRect) return false;
    var r = el.getBoundingClientRect();
    var st = window.getComputedStyle(el);
    return r.width > 0 && r.height > 0 && st.visibility !== "hidden" && st.display !== "none";
  }

  async function waitFor(fn, timeout, interval) {
    timeout = timeout == null ? 4000 : timeout;
    interval = interval == null ? 50 : interval;
    var start = Date.now();
    var last;
    while (Date.now() - start < timeout) {
      last = fn();
      if (last) return last;
      await sleep(interval);
    }
    return last || null;
  }

  function fireMouse(el, type) {
    el.dispatchEvent(
      new MouseEvent(type, { bubbles: true, cancelable: true, view: window, buttons: 1 })
    );
  }

  function clickEl(el) {
    if (!el) return;
    el.focus && el.focus();
    fireMouse(el, "pointerdown");
    fireMouse(el, "mousedown");
    fireMouse(el, "pointerup");
    fireMouse(el, "mouseup");
    fireMouse(el, "click");
  }

  function setNativeValue(el, value) {
    var proto =
      el.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    var desc = Object.getOwnPropertyDescriptor(proto, "value");
    if (desc && desc.set) desc.set.call(el, value);
    else el.value = value;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function labelText(el) {
    return (el && (el.innerText || el.textContent) || "").replace(/\s+/g, " ").trim();
  }

  /**
   * Trouve l'input/textarea MUI (ou natif) associé à un libellé français.
   * Les deux AddressField ont le même id : on ne s'appuie pas sur l'id.
   */
  function findField(labelWanted) {
    var want = normalize(labelWanted);
    var labels = document.querySelectorAll("label, .MuiInputLabel-root, [class*='MuiInputLabel']");
    for (var i = 0; i < labels.length; i++) {
      var lab = labels[i];
      if (!visible(lab) && lab.offsetParent === null) continue;
      if (normalize(labelText(lab)) !== want && normalize(labelText(lab)).indexOf(want) === -1) {
        continue;
      }
      // Mobilic pose le même id="address-field" sur les deux lieux : ne pas
      // utiliser getElementById (ça renverrait toujours le premier).
      var root =
        lab.closest(".MuiTextField-root, .MuiFormControl-root, .MuiAutocomplete-root") ||
        lab.parentElement;
      if (root) {
        var inp = root.querySelector("input:not([type='hidden']), textarea");
        if (inp) return inp;
      }
      if (lab.htmlFor) {
        var matches = document.querySelectorAll("#" + CSS.escape(lab.htmlFor) + ", [id='" + lab.htmlFor.replace(/'/g, "\\'") + "']");
        if (matches.length === 1 && (matches[0].tagName === "INPUT" || matches[0].tagName === "TEXTAREA")) {
          return matches[0];
        }
      }
    }
    var inputs = document.querySelectorAll("input, textarea");
    for (var j = 0; j < inputs.length; j++) {
      var el = inputs[j];
      if (!visible(el)) continue;
      var ac = el.getAttribute("aria-label") || "";
      if (normalize(ac) === want || normalize(ac).indexOf(want) !== -1) return el;
      if (el.id) {
        var lab2 = document.querySelector('label[for="' + el.id + '"]');
        if (lab2 && (normalize(labelText(lab2)) === want || normalize(labelText(lab2)).indexOf(want) !== -1)) {
          return el;
        }
      }
    }
    return null;
  }

  function formatFr(date) {
    var dd = String(date.getDate()).padStart(2, "0");
    var mm = String(date.getMonth() + 1).padStart(2, "0");
    return dd + "/" + mm + "/" + date.getFullYear();
  }

  function parseDate(raw, fallbackToday) {
    if (fallbackToday !== false && (!raw || String(raw).trim() === "")) {
      var t = new Date();
      return new Date(t.getFullYear(), t.getMonth(), t.getDate());
    }
    var s = String(raw).trim();
    var m = s.match(/^(\d{4})-(\d{2})-(\d{2})$/);
    if (m) return new Date(+m[1], +m[2] - 1, +m[3]);
    var m2 = s.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})$/);
    if (m2) return new Date(+m2[3], +m2[2] - 1, +m2[1]);
    var d = new Date(s);
    if (!isNaN(d.getTime())) return new Date(d.getFullYear(), d.getMonth(), d.getDate());
    return null;
  }

  function resolveDate(settings) {
    if (settings.useToday !== false && !settings.missionDate) {
      return parseDate("", true);
    }
    if (settings.useToday && !settings.missionDate) return parseDate("", true);
    return parseDate(settings.missionDate, true);
  }

  function inputAlreadyLooksLike(el, expected) {
    if (!el) return false;
    var got = normalize(el.value);
    var want = normalize(expected);
    return got === want || (want && got.indexOf(want) !== -1);
  }

  function findOpenListbox() {
    var boxes = document.querySelectorAll('[role="listbox"], ul.MuiAutocomplete-listbox, .MuiAutocomplete-listbox');
    for (var i = 0; i < boxes.length; i++) {
      if (visible(boxes[i])) return boxes[i];
    }
    // Portail MUI : le popper peut être visible avant le listbox.
    var poppers = document.querySelectorAll(".MuiAutocomplete-popper, .MuiPopper-root");
    for (var j = 0; j < poppers.length; j++) {
      if (!visible(poppers[j])) continue;
      var inner = poppers[j].querySelector('[role="listbox"], .MuiAutocomplete-listbox');
      if (inner) return inner;
      return poppers[j];
    }
    return null;
  }

  function optionMatches(el, needle) {
    var want = normalize(needle);
    if (!want) return false;
    var txt = normalize(el.innerText || el.textContent || "");
    return txt.indexOf(want) !== -1;
  }

  function findOption(listbox, needle) {
    var opts = (listbox || document).querySelectorAll('[role="option"], li.MuiAutocomplete-option, li[data-option-index]');
    var found = [];
    for (var i = 0; i < opts.length; i++) {
      if (optionMatches(opts[i], needle)) found.push(opts[i]);
    }
    if (found.length === 1) return found[0];
    if (found.length > 1) {
      // Préférer l'option la plus précise (texte le plus court contenant le needle).
      found.sort(function (a, b) {
        return (a.innerText || "").length - (b.innerText || "").length;
      });
      return found[0];
    }
    return null;
  }

  function getAutocompleteRoot(input) {
    return (
      input.closest(".MuiAutocomplete-root") ||
      input.closest(".MuiFormControl-root") ||
      input.parentElement
    );
  }

  function clickClear(input) {
    var root = getAutocompleteRoot(input);
    if (!root) return;
    var btn = root.querySelector(
      ".MuiAutocomplete-clearIndicator, button[aria-label*='Effacer' i], button[aria-label*='Clear' i], button[title='Clear']"
    );
    if (btn && visible(btn)) clickEl(btn);
  }

  function clickOpenIndicator(input) {
    var root = getAutocompleteRoot(input);
    if (!root) return false;
    var btn = root.querySelector(
      '.MuiAutocomplete-popupIndicator, button[aria-label*="Ouvrir" i], button[aria-label*="Open" i], button[title="Open"]'
    );
    if (btn) {
      clickEl(btn);
      return true;
    }
    return false;
  }

  async function openAutocomplete(input) {
    input.scrollIntoView({ block: "center", inline: "nearest" });
    await sleep(40);
    input.focus();
    fireMouse(input, "pointerdown");
    fireMouse(input, "mousedown");
    fireMouse(input, "pointerup");
    fireMouse(input, "mouseup");
    fireMouse(input, "click");
    var box = await waitFor(findOpenListbox, 400, 40);
    if (box) return box;
    clickOpenIndicator(input);
    box = await waitFor(findOpenListbox, 1200, 40);
    if (box) return box;
    // Dernier essai : ArrowDown ouvre souvent la liste MUI.
    input.focus();
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", code: "ArrowDown", bubbles: true, cancelable: true }));
    input.dispatchEvent(new KeyboardEvent("keyup", { key: "ArrowDown", code: "ArrowDown", bubbles: true, cancelable: true }));
    return waitFor(findOpenListbox, 1200, 40);
  }

  async function closeAutocomplete(input) {
    if (input) {
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", code: "Escape", bubbles: true, cancelable: true }));
      input.dispatchEvent(new KeyboardEvent("keyup", { key: "Escape", code: "Escape", bubbles: true, cancelable: true }));
    }
    await waitFor(function () {
      return !findOpenListbox();
    }, 800, 40);
  }

  /**
   * Ouvre la liste et clique une option. Ne tape rien dans le champ
   * (sinon AddressField bascule en recherche d'adresse et masque « Adresses enregistrées »).
   */
  async function selectAutocompleteOption(input, needle, opts) {
    opts = opts || {};
    if (inputAlreadyLooksLike(input, needle) && !opts.force) {
      return { ok: true, already: true };
    }
    if (input.value && !opts.dontClear) {
      clickClear(input);
      await sleep(120);
    }
    var listbox = await openAutocomplete(input);
    if (!listbox) {
      return { ok: false, error: "liste déroulante introuvable" };
    }
    var option = await waitFor(function () {
      return findOption(listbox, needle) || findOption(document, needle);
    }, opts.timeout || 2500, 60);
    if (!option) {
      await closeAutocomplete(input);
      return { ok: false, error: "option « " + needle + " » introuvable dans la liste" };
    }
    option.scrollIntoView({ block: "nearest" });
    await sleep(30);
    // MUI Autocomplete sélectionne au mousedown, pas seulement au click.
    clickEl(option);
    await sleep(80);
    if (inputAlreadyLooksLike(input, needle)) return { ok: true };
    // Laisser React appliquer la valeur.
    var ok = await waitFor(function () {
      return inputAlreadyLooksLike(input, needle) ? true : null;
    }, 1500, 50);
    if (ok) return { ok: true };
    return { ok: false, error: "l'option a été cliquée mais le champ n'a pas pris la valeur" };
  }

  async function fillPlain(input, value) {
    if (!input) return { ok: false, error: "champ introuvable" };
    if (inputAlreadyLooksLike(input, value)) return { ok: true, already: true };
    input.focus();
    setNativeValue(input, value);
    input.blur && input.blur();
    await sleep(50);
    if (inputAlreadyLooksLike(input, value)) return { ok: true };
    setNativeValue(input, value);
    await sleep(50);
    return inputAlreadyLooksLike(input, value)
      ? { ok: true }
      : { ok: false, error: "la valeur n'a pas été prise (champ contrôlé ?)" };
  }

  function pad2(n) {
    return String(n).padStart(2, "0");
  }

  function parseTimeHHmm(raw) {
    var m = String(raw == null ? "" : raw).trim().match(/^(\d{1,2})\s*[h:]\s*(\d{2})$/);
    if (!m) return { h: 6, m: 0 };
    return { h: Math.min(23, Math.max(0, +m[1])), m: Math.min(59, Math.max(0, +m[2])) };
  }

  function formatHHmm(hm) {
    hm = hm || { h: 6, m: 0 };
    return pad2(hm.h) + ":" + pad2(hm.m);
  }

  function inputTypeOf(el) {
    return String((el && (el.getAttribute("type") || el.type)) || "text").toLowerCase();
  }

  function isNativeDateInput(el) {
    var t = inputTypeOf(el);
    return t === "date" || t === "datetime-local" || t === "datetime" || t === "time";
  }

  function inputLooksLikeDate(el, date, hm) {
    if (!el || !date) return false;
    var v = String(el.value || "").trim();
    if (!v) return false;
    var iso = isoLocalDate(date);
    if (v === iso || v.indexOf(iso) === 0) {
      if (!hm) return true;
      return v.indexOf(formatHHmm(hm)) !== -1;
    }
    var fr = formatFr(date);
    if (normalize(v).indexOf(normalize(fr)) !== -1) {
      if (!hm) return true;
      return v.indexOf(formatHHmm(hm)) !== -1 || v.indexOf(String(hm.h) + ":" + pad2(hm.m)) !== -1;
    }
    return inputAlreadyLooksLike(el, fr);
  }

  function applyNativeDateValue(input, date, hm) {
    var type = inputTypeOf(input);
    var iso = isoLocalDate(date);
    var value;
    if (type === "datetime-local" || type === "datetime") {
      hm = hm || { h: 0, m: 0 };
      value = iso + "T" + formatHHmm(hm);
    } else if (type === "date") {
      value = iso;
    } else if (type === "time") {
      hm = hm || { h: 6, m: 0 };
      value = formatHHmm(hm);
    } else {
      return null;
    }
    setNativeValue(input, value);
    return value;
  }

  function isOutsideMonthDay(el) {
    var c = String((el && el.className) || "");
    if (/outsideCurrentMonth|dayOutsideMonth|outside-month|hiddenDaySpacingFiller|hiddenDayFiller|dayHidden/i.test(c)) return true;
    if (el.getAttribute("aria-hidden") === "true") return true;
    return false;
  }

  function isDisabledEl(el) {
    return !!(
      el.disabled ||
      el.getAttribute("aria-disabled") === "true" ||
      /Mui-disabled/.test(String(el.className || ""))
    );
  }

  function listInMonthDayButtons(root) {
    if (!root) return [];
    var nodes = root.querySelectorAll(
      "button.MuiPickersDay-root, button[class*='MuiPickersDay'], button[role='gridcell'], [role='gridcell']"
    );
    var out = [];
    for (var i = 0; i < nodes.length; i++) {
      var b = nodes[i];
      if (b.tagName === "DIV" || isOutsideMonthDay(b) || isDisabledEl(b) || !visible(b)) continue;
      if (!(b.textContent || "").trim() && !b.getAttribute("aria-label")) continue;
      out.push(b);
    }
    return out;
  }

  function pickerHasCalendar(el) {
    if (!el) return false;
    return !!(
      el.querySelector(
        ".MuiDateCalendar-root, .MuiCalendarPicker-root, .MuiPickersDay-root, [class*='MuiDateCalendar'], [class*='MuiPickersDay'], .MuiMultiSectionDigitalClock-root, [class*='DigitalClock'], [class*='MuiDayCalendar']"
      ) ||
      listInMonthDayButtons(el).length
    );
  }

  function isPickerDialog(el) {
    if (!el || !visible(el)) return false;
    return pickerHasCalendar(el);
  }

  function findDateDialog() {
    var nodes = document.querySelectorAll(
      '[role="dialog"], [aria-modal="true"], .MuiPickersPopper-root, .MuiPickersLayout-root, .MuiDialog-root, .MuiPickersModalDialog-dialog, .MuiDateCalendar-root, .MuiCalendarPicker-root'
    );
    var found = [];
    for (var i = 0; i < nodes.length; i++) {
      if (isPickerDialog(nodes[i])) found.push(nodes[i]);
    }
    if (!found.length) return null;
    // Le formulaire « mission passée » est déjà dans un MuiDialog : on prend
    // le nœud le plus précis (celui qui contient vraiment les jours).
    found.sort(function (a, b) {
      return listInMonthDayButtons(b).length - listInMonthDayButtons(a).length;
    });
    if (listInMonthDayButtons(found[0]).length) return found[0];
    return found[0];
  }

  function getHeaderText(dialog) {
    if (!dialog) return "";
    var header = dialog.querySelector(
      ".MuiPickersCalendarHeader-label, [class*='MuiPickersCalendarHeader-label']"
    );
    return ((header && (header.innerText || header.textContent)) || "").trim();
  }

  function parseHeader(text) {
    var t = normalize(text).replace(/\./g, " ").replace(/\s+/g, " ");
    for (var i = 0; i < MONTHS_FR.length; i++) {
      var full = normalize(MONTHS_FR[i]);
      var shortn = SHORT_MONTHS_FR[i];
      var re = new RegExp("(?:^|\\s)(?:" + full + "|" + shortn + ")\\s+(\\d{4})");
      var hit = t.match(re);
      if (hit) return { m: i, y: +hit[1] };
    }
    return null;
  }

  function headerShowsMonth(dialog, date) {
    var parsed = parseHeader(getHeaderText(dialog) || (dialog.innerText || ""));
    if (parsed) return parsed.m === date.getMonth() && parsed.y === date.getFullYear();
    var wantMonth = normalize(MONTHS_FR[date.getMonth()]);
    var wantYear = String(date.getFullYear());
    var txt = normalize(dialog.innerText || "");
    return txt.indexOf(wantMonth) !== -1 && txt.indexOf(wantYear) !== -1;
  }

  async function goToMonth(dialog, date) {
    if (headerShowsMonth(dialog, date)) return true;
    // Vue année / mois : ne cliquer que dans ces grilles, pas tous les boutons du dialogue.
    var yearBtns = dialog.querySelectorAll(
      "[class*='MuiYearCalendar'] button, [class*='PrivatePickersYear'] button, button[class*='MuiPickersYear']"
    );
    var wantYear = String(date.getFullYear());
    for (var y = 0; y < yearBtns.length; y++) {
      if (normalize(yearBtns[y].textContent || "") === wantYear && !isDisabledEl(yearBtns[y])) {
        clickEl(yearBtns[y]);
        await sleep(200);
        break;
      }
    }
    var monthBtns = dialog.querySelectorAll(
      "[class*='MuiMonthCalendar'] button, [class*='PrivatePickersMonth'] button, button[class*='MuiPickersMonth']"
    );
    var wantM = normalize(MONTHS_FR[date.getMonth()]);
    var wantShort = SHORT_MONTHS_FR[date.getMonth()];
    for (var i = 0; i < monthBtns.length; i++) {
      var t = normalize(monthBtns[i].textContent || "").replace(/\./g, "");
      if ((t === wantM || t === wantShort || t.indexOf(wantM) === 0) && !isDisabledEl(monthBtns[i])) {
        clickEl(monthBtns[i]);
        await sleep(200);
        break;
      }
    }
    for (var step = 0; step < 14; step++) {
      if (headerShowsMonth(dialog, date)) return true;
      var prev = dialog.querySelector(
        'button[aria-label*="précédent" i], button[title*="précédent" i], button[aria-label*="previous" i], button[title*="Previous" i]'
      );
      var next = dialog.querySelector(
        'button[aria-label*="suivant" i], button[title*="suivant" i], button[aria-label*="next" i], button[title*="Next" i]'
      );
      var shown = parseHeader(getHeaderText(dialog) || (dialog.innerText || ""));
      var btn = null;
      if (shown) {
        var shownIdx = shown.y * 12 + shown.m;
        var wantIdx = date.getFullYear() * 12 + date.getMonth();
        if (shownIdx === wantIdx) return true;
        btn = wantIdx < shownIdx ? prev : next;
      } else {
        btn = prev || next;
      }
      if (!btn || isDisabledEl(btn)) break;
      clickEl(btn);
      await sleep(180);
    }
    return headerShowsMonth(dialog, date);
  }

  function ariaLabelMatchesDate(al, date) {
    if (!al) return false;
    var compact = normalize(al).replace(/\./g, " ").replace(/\s+/g, " ");
    var year = String(date.getFullYear());
    if (compact.indexOf(year) === -1) return false;
    var day = String(date.getDate());
    var full = normalize(MONTHS_FR[date.getMonth()]);
    var shortn = SHORT_MONTHS_FR[date.getMonth()];
    var hasMonth = compact.indexOf(full) !== -1 || compact.indexOf(shortn) !== -1;
    var hasDay =
      compact.indexOf(day + " ") !== -1 ||
      compact.indexOf(" " + day + " ") !== -1 ||
      compact.indexOf(pad2(date.getDate())) !== -1 ||
      compact.indexOf(formatFr(date)) !== -1;
    return hasMonth && hasDay;
  }

  function findDayButton(dialog, date) {
    var day = date.getDate();
    var buttons = listInMonthDayButtons(dialog);
    var byLabel = [];
    var byNumber = [];
    for (var i = 0; i < buttons.length; i++) {
      var b = buttons[i];
      if (ariaLabelMatchesDate(b.getAttribute("aria-label") || "", date)) byLabel.push(b);
      var num = parseInt(String(b.textContent || "").replace(/\D+/g, " ").trim(), 10);
      if (num === day) byNumber.push(b);
    }
    if (byLabel.length) return byLabel[0];
    if (byNumber.length === 1) return byNumber[0];
    if (
      date.getDate() === new Date().getDate() &&
      date.getMonth() === new Date().getMonth() &&
      date.getFullYear() === new Date().getFullYear()
    ) {
      var todayBtn = dialog.querySelector(
        "button.MuiPickersDay-today, button[class*='MuiPickersDay-today'], button[aria-current='date']"
      );
      if (todayBtn && !isOutsideMonthDay(todayBtn) && !isDisabledEl(todayBtn)) return todayBtn;
    }
    return byNumber[0] || null;
  }

  function clickPickerOk(dialog) {
    var buttons = dialog.querySelectorAll("button");
    for (var i = 0; i < buttons.length; i++) {
      var t = (buttons[i].textContent || "").trim();
      if (/^(OK|Valider|Accept)$/i.test(t)) {
        clickEl(buttons[i]);
        return true;
      }
    }
    var actions = dialog.querySelector(
      ".MuiDialogActions-root, [class*='MuiDialogActions'], [class*='PickersActionBar']"
    );
    if (actions) {
      var actBtns = actions.querySelectorAll("button");
      if (actBtns.length) {
        clickEl(actBtns[actBtns.length - 1]);
        return true;
      }
    }
    return false;
  }

  function clickClockSection(dialog, value, sectionHint) {
    var want = pad2(value);
    var wantAlt = String(value);
    var items = dialog.querySelectorAll(
      "[role='option'], .MuiMultiSectionDigitalClockSection-item, [class*='MultiSectionDigitalClockSection-item'], [class*='DigitalClockSection-item'], [class*='MuiMultiSectionDigitalClock'] li, [class*='MuiMultiSectionDigitalClock'] button"
    );
    var pool = [];
    for (var i = 0; i < items.length; i++) {
      if (sectionHint && items[i].closest && sectionHint(items[i]) === false) continue;
      pool.push(items[i]);
    }
    if (!pool.length) pool = Array.prototype.slice.call(items);
    for (var j = 0; j < pool.length; j++) {
      var t = (pool[j].textContent || "").trim();
      if (t === want || t === wantAlt) {
        clickEl(pool[j]);
        return true;
      }
    }
    return false;
  }

  function describePicker(dialog) {
    if (!dialog) return "dialog=non";
    var header = getHeaderText(dialog) || "(sans en-tête)";
    var days = listInMonthDayButtons(dialog);
    var samples = [];
    for (var i = 0; i < days.length && samples.length < 6; i++) {
      samples.push((days[i].getAttribute("aria-label") || (days[i].textContent || "").trim()));
    }
    var parts = ["dialog=oui", 'header="' + header.replace(/\s+/g, " ") + '"', "boutonsJour=" + days.length];
    if (samples.length) parts.push("ex=[" + samples.join(", ") + "]");
    var clock = dialog.querySelector("[class*='DigitalClock'], [role='listbox']");
    if (clock) parts.push("horloge=oui");
    return parts.join(", ");
  }

  function logPicker(msg, extra) {
    try {
      console.log("[LiteBrowser Mobilic]", msg, extra || "");
    } catch (e) {}
  }

  function openPickerField(input) {
    if (!input) return;
    input.scrollIntoView({ block: "center", inline: "nearest" });
    var root =
      input.closest(".MuiFormControl-root, .MuiTextField-root, .MuiPickersTextField-root") ||
      input.parentElement;
    var icon = root
      ? root.querySelector(
          "button[aria-label*='choisir' i], button[aria-label*='date' i], button[aria-label*='calendar' i], button[aria-label*='ouvrir' i], .MuiInputAdornment-root button"
        )
      : null;
    clickEl(icon || input);
  }

  async function tryTypeMuiSections(input, date, hm) {
    var root =
      input.closest(".MuiFormControl-root, .MuiTextField-root, .MuiPickersTextField-root") ||
      input.parentElement;
    if (!root) return { ok: false };
    var sections = root.querySelectorAll(
      "[data-sectionindex], .MuiPickersSectionList-section, [class*='MuiPickersSectionList-section']"
    );
    if (!sections.length) return { ok: false };
    var digits = pad2(date.getDate()) + pad2(date.getMonth() + 1) + String(date.getFullYear());
    if (hm) digits += formatHHmm(hm).replace(":", "");
    clickEl(sections[0]);
    await sleep(30);
    var target = document.activeElement || sections[0];
    for (var i = 0; i < digits.length; i++) {
      var ev = { key: digits[i], code: "Digit" + digits[i], keyCode: 48 + Number(digits[i]), which: 48 + Number(digits[i]), bubbles: true, cancelable: true };
      target.dispatchEvent(new KeyboardEvent("keydown", ev));
      target.dispatchEvent(new KeyboardEvent("keyup", ev));
      if (typeof document.execCommand === "function") {
        try { document.execCommand("insertText", false, digits[i]); } catch (e) {}
      }
      await sleep(20);
    }
    await sleep(80);
    return { ok: inputLooksLikeDate(input, date, hm), method: "sections" };
  }

  async function fillDate(input, date, hm) {
    if (!input) return { ok: false, error: "champ date introuvable" };
    var expected = formatFr(date);
    if (inputLooksLikeDate(input, date, hm)) return { ok: true, already: true };

    // 1) Champ natif (date / datetime-local / time). Le calendrier Chromium
    //    est dans le shadow DOM du navigateur : on ne peut pas cliquer les jours.
    if (isNativeDateInput(input)) {
      applyNativeDateValue(input, date, hm);
      await sleep(40);
      if (inputLooksLikeDate(input, date, hm)) return { ok: true, method: "native" };
      return {
        ok: false,
        error: "valeur native refusée pour " + expected + (hm ? " " + formatHHmm(hm) : ""),
      };
    }

    // 2) Champ MUI à sections (DateField / DateTimeField v6) : taper les chiffres.
    var typed = await tryTypeMuiSections(input, date, hm);
    if (typed.ok) return typed;

    // 3) Saisie directe si le champ n'est pas en lecture seule.
    if (!input.readOnly && !input.disabled && inputTypeOf(input) !== "hidden") {
      var asText = expected + (hm ? " " + formatHHmm(hm) : "");
      var plain = await fillPlain(input, asText);
      if (plain.ok || inputLooksLikeDate(input, date, hm)) return { ok: true, method: "input" };
    }

    // 4) Calendrier MUI (dialogue ou popover) — pas le dialogue du formulaire.
    input.scrollIntoView({ block: "center", inline: "nearest" });
    await sleep(40);
    openPickerField(input);
    var dialog = await waitFor(findDateDialog, 2500, 50);
    var diag = describePicker(dialog);
    logPicker("calendrier", diag);
    if (!dialog) {
      return {
        ok: false,
        error: "calendrier introuvable (MobileDatePicker). " + diag + ". Choisis la date à la main si besoin.",
      };
    }
    await goToMonth(dialog, date);
    dialog = findDateDialog() || dialog;
    diag = describePicker(dialog);
    var dayBtn = await waitFor(function () {
      return findDayButton(dialog, date);
    }, 2000, 50);
    if (!dayBtn) {
      logPicker("jour introuvable", diag);
      return {
        ok: false,
        error: "jour " + expected + " introuvable dans le calendrier (" + diag + ")",
      };
    }
    clickEl(dayBtn);
    await sleep(80);
    if (hm) {
      clickClockSection(dialog, hm.h);
      await sleep(50);
      clickClockSection(dialog, hm.m);
      await sleep(50);
    }
    clickPickerOk(dialog);
    var ok = await waitFor(function () {
      return inputLooksLikeDate(input, date, hm) ? true : null;
    }, 2000, 50);
    if (ok) return { ok: true, method: "picker" };
    return {
      ok: false,
      error: "le calendrier a été cliqué mais la date affichée n'est pas " + expected + " (" + diag + ")",
    };
  }

  async function fillVehicle(input, value) {
    if (!input) return { ok: false, error: "champ véhicule introuvable" };
    if (inputAlreadyLooksLike(input, value)) return { ok: true, already: true };
    var selected = await selectAutocompleteOption(input, value, { timeout: 1500 });
    if (selected.ok) return { ok: true, method: "option" };
    // Pas d'option : saisie freeSolo (VehicleField accepte une immatriculation libre).
    await closeAutocomplete(input);
    var typed = await fillPlain(input, value);
    if (typed.ok) return { ok: true, method: "input" };
    return { ok: false, error: selected.error || typed.error };
  }

  function formLooksPresent() {
    return !!(
      findField("Nom de la mission") ||
      findField("Lieu de prise de service") ||
      findField("Jour de la mission")
    );
  }

  async function waitForForm(timeout) {
    timeout = timeout || 6000;
    var found = await waitFor(formLooksPresent, timeout, 100);
    return !!found;
  }

  /**
   * @param {object} settings
   * @returns {Promise<{ok:boolean, filled:string[], failed:{field:string,error:string}[], date:string}>}
   */
  async function fill(settings) {
    settings = Object.assign({}, DEFAULTS, settings || {});
    var filled = [];
    var failed = [];
    var date = resolveDate(settings);
    var dateLabel = date ? formatFr(date) : "";

    function rec(field, result) {
      if (result && result.ok) filled.push(field);
      else failed.push({ field: field, error: (result && result.error) || "échec" });
    }

    if (!formLooksPresent()) {
      var appeared = await waitForForm(5000);
      if (!appeared) {
        return {
          ok: false,
          filled: [],
          failed: [
            {
              field: "formulaire",
              error:
                "Formulaire introuvable. Ouvre d'abord « Ajouter une mission passée » sur Mobilic, puis réessaie.",
            },
          ],
          date: dateLabel,
        };
      }
    }

    // Entreprise (si plusieurs) : les adresses sont désactivées tant qu'elle n'est pas choisie.
    var company = findField("Entreprise");
    if (company && !company.value && !company.disabled) {
      failed.push({
        field: "entreprise",
        error: "Choisis d'abord l'entreprise dans le formulaire, puis relance le pré-remplissage.",
      });
    }

    var nameInput = findField("Nom de la mission");
    rec("nom de la mission", await fillPlain(nameInput, settings.missionName));

    var dateInput = findField("Jour de la mission");
    if (dateInput && date) rec("jour de la mission (" + dateLabel + ")", await fillDate(dateInput, date));
    else if (!dateInput) rec("jour de la mission", { ok: false, error: "champ introuvable" });
    else rec("jour de la mission", { ok: false, error: "date invalide" });

    var startHm = parseTimeHHmm(settings.startTime || "06:00");
    var startInput = findField("Début");
    if (startInput && date) {
      rec("heure de début (" + formatHHmm(startHm) + ")", await fillDate(startInput, date, startHm));
    }

    var startInput = findField("Lieu de prise de service");
    if (startInput && startInput.disabled) {
      rec("lieu de prise de service", {
        ok: false,
        error: "champ désactivé (choisis l'entreprise d'abord)",
      });
    } else {
      rec(
        "lieu de prise de service",
        await selectAutocompleteOption(startInput, settings.startLocation)
      );
    }

    var endInput = findField("Lieu de fin de service");
    if (!endInput) {
      rec("lieu de fin de service", { ok: false, error: "champ introuvable" });
    } else if (endInput.disabled) {
      rec("lieu de fin de service", {
        ok: false,
        error: "champ désactivé (choisis l'entreprise d'abord)",
      });
    } else {
      rec(
        "lieu de fin de service",
        await selectAutocompleteOption(endInput, settings.endLocation)
      );
    }

    var vehicleInput = findField("Nom ou immatriculation du véhicule");
    rec("véhicule", await fillVehicle(vehicleInput, settings.vehicle));

    var reasonInput = findField("Raison de l'ajout d'une mission passée");
    rec("motif", await fillPlain(reasonInput, settings.reason));

    return {
      ok: failed.length === 0,
      filled: filled,
      failed: failed,
      date: dateLabel,
    };
  }


  var SHORT_MONTHS_FR = ["jan", "fev", "mar", "avr", "mai", "juin", "juil", "aou", "sep", "oct", "nov", "dec"];
  var DAY_TILE_RE = /^(\d{1,2})\s+(jan|fev|mar|avr|mai|juin|juil|aou|sep|oct|nov|dec)\s+(lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche)$/;

  function foldText(s) {
    return String(s || "")
      .toLowerCase()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/\s+/g, " ")
      .trim();
  }

  function parseHistoryDayTile(text) {
    var m = foldText(text).match(DAY_TILE_RE);
    if (!m) return null;
    var day = +m[1];
    var month = SHORT_MONTHS_FR.indexOf(m[2]);
    if (day < 1 || day > 31 || month < 0) return null;
    return { day: day, month: month, weekday: m[3] };
  }

  function inferHistoryYear(month, now) {
    now = now || new Date();
    var year = now.getFullYear();
    if (month > now.getMonth()) return year - 1;
    return year;
  }

  function historyDateFromParts(parts, now) {
    if (!parts) return null;
    now = now || new Date();
    var year = inferHistoryYear(parts.month, now);
    var d = new Date(year, parts.month, parts.day);
    if (d.getFullYear() !== year || d.getMonth() !== parts.month || d.getDate() !== parts.day) return null;
    return d;
  }

  function startOfLocalDay(d) {
    return new Date(d.getFullYear(), d.getMonth(), d.getDate());
  }

  function isMobilicDateAllowed(date, now) {
    now = now || new Date();
    if (!date || isNaN(date.getTime())) return false;
    var today = startOfLocalDay(now);
    var min = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 30);
    var d = startOfLocalDay(date);
    return d >= min && d <= today;
  }

  function isoLocalDate(date) {
    return date.getFullYear() + "-" + String(date.getMonth() + 1).padStart(2, "0") + "-" + String(date.getDate()).padStart(2, "0");
  }

  var api = {
    DEFAULTS: DEFAULTS,
    fill: fill,
    findField: findField,
    formatFr: formatFr,
    parseDate: parseDate,
    resolveDate: resolveDate,
    waitForForm: waitForForm,
    formLooksPresent: formLooksPresent,
    parseHistoryDayTile: parseHistoryDayTile,
    inferHistoryYear: inferHistoryYear,
    historyDateFromParts: historyDateFromParts,
    isMobilicDateAllowed: isMobilicDateAllowed,
    isoLocalDate: isoLocalDate,
  };

  root.MobilicPrefill = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})(typeof window !== "undefined" ? window : globalThis);


  var STORAGE_KEY = "litebrowser.mobilic.prefill";
  var BTN_ID = "mobilic-prefill-fab";
  var GEAR_ID = "mobilic-prefill-gear";
  var PANEL_ID = "mobilic-prefill-panel";
  var BACKDROP_ID = "mobilic-prefill-backdrop";
  var TOAST_ID = "mobilic-prefill-toast";
  var STYLE_ID = "mobilic-prefill-style";
  var dateGuardUntil = 0;

  function injectCss() {
    if (document.getElementById(STYLE_ID)) return;
    var st = document.createElement("style");
    st.id = STYLE_ID;
    st.textContent = CSS_TEXT;
    (document.head || document.documentElement).appendChild(st);
  }

  function todayISO() {
    var d = new Date();
    return d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
  }

  function daysAgoISO(n) {
    var d = new Date();
    d.setDate(d.getDate() - n);
    return d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
  }

  function loadSettings() {
    var base = Object.assign({}, window.MobilicPrefill.DEFAULTS);
    try {
      var raw = localStorage.getItem(STORAGE_KEY);
      if (raw) Object.assign(base, JSON.parse(raw));
    } catch (e) {}
    return base;
  }

  function saveSettings(data) {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(data)); } catch (e) {}
  }

  function showToast(result) {
    var old = document.getElementById(TOAST_ID);
    if (old) old.remove();
    var el = document.createElement("div");
    el.id = TOAST_ID;
    el.setAttribute("role", "status");
    var ok = result && result.ok && (!result.failed || !result.failed.length);
    el.className = "mobilic-prefill-toast " + (ok ? "is-ok" : "is-partial");
    var parts = [];
    if (result.filled && result.filled.length) parts.push("Pré-rempli : " + result.filled.join(", ") + ".");
    if (result.failed && result.failed.length) {
      parts.push("Échec : " + result.failed.map(function (f) { return f.field + " (" + f.error + ")"; }).join(" · ") + ".");
    }
    if (!parts.length) parts.push("Rien à pré-remplir.");
    parts.push("Rien n'a été envoyé : clique toi-même sur Continuer.");
    el.textContent = parts.join(" ");
    document.documentElement.appendChild(el);
    setTimeout(function () { el.classList.add("is-out"); setTimeout(function () { el.remove(); }, 400); }, 8000);
  }

  async function runFill() {
    var btn = document.getElementById(BTN_ID);
    if (btn) { btn.disabled = true; btn.classList.add("is-busy"); btn.textContent = "Pré-remplissage…"; }
    try {
      var result = await window.MobilicPrefill.fill(loadSettings());
      showToast(result);
      return result;
    } catch (err) {
      var fail = { ok: false, filled: [], failed: [{ field: "extension", error: String((err && err.message) || err) }] };
      showToast(fail);
      return fail;
    } finally {
      if (btn) { btn.disabled = false; btn.classList.remove("is-busy"); btn.textContent = "Pré-remplir"; }
    }
  }

  function markDateGuard() {
    dateGuardUntil = Date.now() + 600;
  }

  function isDateInput(el) {
    return !!(el && el.id === "mp-date");
  }

  function saveFromPanel() {
    var panel = document.getElementById(PANEL_ID);
    if (!panel) return;
    var dateEl = panel.querySelector("#mp-date");
    var date = dateEl ? dateEl.value : "";
    var isToday = !date || date === todayISO();
    saveSettings({
      missionName: (panel.querySelector("#mp-name") || {}).value ? panel.querySelector("#mp-name").value.trim() : "",
      useToday: isToday,
      missionDate: isToday ? "" : date,
      startLocation: (panel.querySelector("#mp-start") || {}).value ? panel.querySelector("#mp-start").value.trim() : "",
      endLocation: (panel.querySelector("#mp-end") || {}).value ? panel.querySelector("#mp-end").value.trim() : "",
      vehicle: (panel.querySelector("#mp-veh") || {}).value ? panel.querySelector("#mp-veh").value.trim() : "",
      reason: panel.querySelector("#mp-reason") ? panel.querySelector("#mp-reason").value : "",
      startTime: (panel.querySelector("#mp-start-time") && panel.querySelector("#mp-start-time").value) || "06:00"
    });
  }

  function closePanel() {
    var p = document.getElementById(PANEL_ID);
    if (p) p.remove();
    var b = document.getElementById(BACKDROP_ID);
    if (b) b.remove();
    document.removeEventListener("keydown", onDocKeydown, true);
    document.removeEventListener("pointerdown", onDocPointerDown, true);
  }

  function onDocKeydown(e) {
    if (!e || e.key !== "Escape") return;
    if (!document.getElementById(PANEL_ID)) return;
    if (isDateInput(document.activeElement)) {
      markDateGuard();
      document.activeElement.blur();
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    e.preventDefault();
    e.stopPropagation();
    saveFromPanel();
    closePanel();
  }

  function onDocPointerDown(e) {
    if (!document.getElementById(PANEL_ID)) return;
    var t = e && e.target;
    if (t && t.closest) {
      if (t.closest("#" + PANEL_ID) || t.closest("#" + BTN_ID) || t.closest("#" + GEAR_ID) || t.closest("#" + TOAST_ID)) {
        return;
      }
    }
    // Le sélecteur de date natif peut envoyer des clics « hors panneau » tant
    // que le champ est focus : on ne ferme pas dans ce cas, pour ne pas
    // interrompre le choix de la date. Une fois le champ relâché, un clic
    // extérieur (fond compris) ferme bien le panneau.
    if (isDateInput(document.activeElement) || isDateInput(t)) {
      markDateGuard();
      return;
    }
    saveFromPanel();
    closePanel();
  }

  function openPanel() {
    if (document.getElementById(PANEL_ID)) return;
    var s = loadSettings();
    var backdrop = document.createElement("div");
    backdrop.id = BACKDROP_ID;
    backdrop.className = "mobilic-prefill-backdrop";
    backdrop.setAttribute("data-mobilic-prefill", "backdrop");
    document.documentElement.appendChild(backdrop);

    var panel = document.createElement("div");
    panel.id = PANEL_ID;
    panel.className = "mobilic-prefill-panel";
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "Réglages Mobilic");
    panel.innerHTML =
      "<header><div class='title-row'><h2>Réglages Mobilic</h2>" +
      "<button type='button' class='icon-close' id='mp-x' aria-label='Fermer' title='Fermer'>×</button></div>" +
      "<p>Enregistrés dans ce navigateur. Rien n'est envoyé à Mobilic.</p></header>" +
      "<div class='body'>" +
      "<label for='mp-name'>Nom de la mission</label><input id='mp-name' type='text' autocomplete='off'>" +
      "<label for='mp-date'>Jour de la mission</label><div class='row'><div><input id='mp-date' type='date'></div>" +
      "<button type='button' class='ghost' id='mp-today'>Aujourd'hui</button></div>" +
      "<p class='hint'>Par défaut : aujourd'hui. Mobilic n'accepte que les 30 derniers jours.</p>" +
      "<label for='mp-start'>Lieu de prise de service</label><input id='mp-start' type='text' autocomplete='off'>" +
      "<p class='hint'>Doit correspondre à une adresse enregistrée. L'outil clique l'option, il ne tape pas l'adresse.</p>" +
      "<label for='mp-end'>Lieu de fin de service</label><input id='mp-end' type='text' autocomplete='off'>" +
      "<label for='mp-veh'>Nom ou immatriculation du véhicule</label><input id='mp-veh' type='text' autocomplete='off'>" +
      "<label for='mp-start-time'>Heure de début</label><input id='mp-start-time' type='time' step='60'>" +
      "<p class='hint'>Pré-remplie sur le champ Début (étape des horaires), par défaut 06:00.</p>" +
      "<label for='mp-reason'>Raison de l'ajout d'une mission passée</label><textarea id='mp-reason' maxlength='48'></textarea>" +
      "<div class='actions'>" +
      "<button type='button' class='primary' id='mp-save-fill'>Enregistrer et pré-remplir</button>" +
      "<button type='button' class='primary' id='mp-save'>Enregistrer</button>" +
      "<button type='button' class='ghost' id='mp-cancel'>Fermer</button>" +
      "</div>" +
      "<div class='status' id='mp-status'></div></div>";
    document.documentElement.appendChild(panel);

    panel.querySelector("#mp-name").value = s.missionName || "";
    panel.querySelector("#mp-start").value = s.startLocation || "";
    panel.querySelector("#mp-end").value = s.endLocation || "";
    panel.querySelector("#mp-veh").value = s.vehicle || "";
    panel.querySelector("#mp-reason").value = s.reason == null ? "" : s.reason;
    panel.querySelector("#mp-start-time").value = s.startTime || "06:00";
    var dateEl = panel.querySelector("#mp-date");
    dateEl.value = (s.useToday !== false && !s.missionDate) ? todayISO() : (s.missionDate || todayISO());
    dateEl.min = daysAgoISO(30);
    dateEl.max = todayISO();
    dateEl.addEventListener("pointerdown", function (e) { e.stopPropagation(); markDateGuard(); });
    dateEl.addEventListener("click", function (e) { e.stopPropagation(); markDateGuard(); });
    dateEl.addEventListener("focus", function () { markDateGuard(); });
    dateEl.addEventListener("input", function () { markDateGuard(); });
    dateEl.addEventListener("change", function () {
      markDateGuard();
      saveFromPanel();
      // Relâche le focus pour que le sélecteur natif ne bloque pas le panneau.
      setTimeout(function () { if (document.activeElement === dateEl) dateEl.blur(); }, 0);
    });
    // Les événements du champ date ne doivent jamais rouvrir le panneau.
    dateEl.addEventListener("blur", function () { markDateGuard(); });

    panel.querySelector("#mp-today").addEventListener("click", function (e) {
      e.preventDefault();
      e.stopPropagation();
      dateEl.value = todayISO();
      saveFromPanel();
    });
    function closeAndSave(e) {
      if (e) { e.preventDefault(); e.stopPropagation(); }
      saveFromPanel();
      closePanel();
    }
    panel.querySelector("#mp-x").addEventListener("click", closeAndSave);
    panel.querySelector("#mp-cancel").addEventListener("click", closeAndSave);
    panel.querySelector("#mp-save").addEventListener("click", function (e) {
      e.preventDefault();
      e.stopPropagation();
      saveFromPanel();
      closePanel();
    });
    panel.querySelector("#mp-save-fill").addEventListener("click", function (e) {
      e.preventDefault();
      e.stopPropagation();
      saveFromPanel();
      closePanel();
      runFill();
    });

    document.addEventListener("keydown", onDocKeydown, true);
    document.addEventListener("pointerdown", onDocPointerDown, true);
  }


  function showInfoToast(message, kind) {
    var old = document.getElementById(TOAST_ID);
    if (old) old.remove();
    var el = document.createElement("div");
    el.id = TOAST_ID;
    el.setAttribute("role", "status");
    el.className = "mobilic-prefill-toast" + (kind === "warn" ? " is-partial" : " is-ok");
    el.textContent = message;
    document.documentElement.appendChild(el);
    setTimeout(function () { el.classList.add("is-out"); setTimeout(function () { el.remove(); }, 400); }, 8000);
  }

  function isOurUiEvent(el) {
    if (!el || !el.closest) return false;
    return !!(
      el.closest("#" + PANEL_ID) ||
      el.closest("#" + BTN_ID) ||
      el.closest("#" + GEAR_ID) ||
      el.closest("#" + TOAST_ID) ||
      el.closest("#" + BACKDROP_ID)
    );
  }

  function isJourTabSelected() {
    var selected = document.querySelector('[role="tab"][aria-selected="true"]');
    if (!selected) return true;
    return /^\s*jour\s*$/i.test((selected.textContent || "").replace(/\s+/g, " ").trim());
  }

  function closestHistoryDayTile(start) {
    var el = start && start.nodeType === 3 ? start.parentElement : start;
    var hops = 0;
    while (el && el !== document && el !== document.documentElement && hops < 20) {
      if (isOurUiEvent(el)) return null;
      var text = el.innerText || el.textContent || "";
      if (window.MobilicPrefill.parseHistoryDayTile(text)) return el;
      el = el.parentElement;
      hops++;
    }
    return null;
  }

  function onHistoryDayClick(e) {
    if (!e || isOurUiEvent(e.target)) return;
    if (!isJourTabSelected()) return;
    var tile = closestHistoryDayTile(e.target);
    if (!tile) return;
    var parts = window.MobilicPrefill.parseHistoryDayTile(tile.innerText || tile.textContent || "");
    if (!parts) return;
    var date = window.MobilicPrefill.historyDateFromParts(parts, new Date());
    if (!date) return;
    var fmt = window.MobilicPrefill.formatFr(date);
    if (!window.MobilicPrefill.isMobilicDateAllowed(date, new Date())) {
      showInfoToast("Date " + fmt + " hors limites Mobilic (J-30 à aujourd'hui). Non enregistrée.", "warn");
      return;
    }
    var iso = window.MobilicPrefill.isoLocalDate(date);
    var isToday = iso === todayISO();
    var s = loadSettings();
    s.useToday = isToday;
    s.missionDate = isToday ? "" : iso;
    saveSettings(s);
    var dateEl = document.getElementById("mp-date");
    if (dateEl) dateEl.value = iso;
    showInfoToast("Date enregistrée : " + fmt);
  }

  function ensureUi() {
    injectCss();
    var root = document.body || document.documentElement;
    if (!root) return;
    if (!document.getElementById(BTN_ID)) {
      var btn = document.createElement("button");
      btn.id = BTN_ID;
      btn.type = "button";
      btn.className = "mobilic-prefill-fab";
      btn.textContent = "Pré-remplir";
      btn.title = "Pré-remplir le formulaire mission passée (n'envoie rien)";
      btn.addEventListener("click", function (e) {
        e.preventDefault();
        e.stopPropagation();
        if (document.getElementById(PANEL_ID)) {
          saveFromPanel();
          closePanel();
        }
        runFill();
      });
      root.appendChild(btn);
    }
    if (!document.getElementById(GEAR_ID)) {
      var gear = document.createElement("button");
      gear.id = GEAR_ID;
      gear.type = "button";
      gear.className = "mobilic-prefill-gear";
      gear.textContent = "Réglages";
      gear.title = "Modifier les valeurs pré-remplies";
      gear.addEventListener("click", function (e) {
        e.preventDefault();
        e.stopPropagation();
        if (document.getElementById(PANEL_ID)) closePanel();
        else openPanel();
      });
      root.appendChild(gear);
    }
  }

  function boot() {
    ensureUi();
    document.addEventListener("click", onHistoryDayClick, true);
    var obs = new MutationObserver(function () {
      if (!document.getElementById(BTN_ID) || !document.getElementById(STYLE_ID)) ensureUi();
    });
    obs.observe(document.documentElement, { childList: true, subtree: true });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();

})();
