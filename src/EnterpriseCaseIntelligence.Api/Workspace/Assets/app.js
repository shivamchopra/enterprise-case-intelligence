"use strict";

const byId = (id) => document.getElementById(id);
const statusNames = { New: "New", InReview: "In review", Closed: "Closed" };
const priorityNames = ["Low", "Normal", "High"];
let listGeneration = 0;
let detailGeneration = 0;
let historyGeneration = 0;
let briefGeneration = 0;
let overviewGeneration = 0;
let priorityGeneration = 0;
let nextAfter = null;
let submittedTitle = null;
let selectedId = null;
let currentCase = null;
let changingStatus = false;
let changingPriority = false;
let currentPriority = null;
let draft = null;
let creating = false;
let uncertain = false;
let sessionGeneration = 0;
let canRead = false;
let canWrite = false;
let csrfToken = null;
let sessionExpiryTimer = null;
let signingOut = false;

function message(id, text, error = false) {
    const element = byId(id);
    element.textContent = text;
    element.dataset.error = String(error);
}

function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text !== undefined) element.textContent = text;
    if (className) element.className = className;
    return element;
}

function badge(status) {
    const element = node("span", statusNames[status], "badge");
    element.dataset.status = status;
    return element;
}

async function request(path, options = {}) {
    const generation = sessionGeneration;
    const headers = { ...options.headers };
    if (options.method && options.method !== "GET" && csrfToken)
        headers["X-ECI-CSRF"] = csrfToken;
    const response = await fetch(path, {
        cache: "no-store",
        credentials: "same-origin",
        redirect: "error",
        signal: AbortSignal.timeout(15000),
        ...options,
        headers,
    });
    if (generation !== sessionGeneration) throw new Error("Session changed");
    if (response.status === 401 || response.status === 403) {
        clearAccess(
            response.status === 401
                ? "Sign in to continue. An unfinished write may already have committed. Signing in leaves this page and loses creation retry context; reconcile saved cases before writing again."
                : "Access denied. Your current session has no permission for this action. Refresh access or sign out.",
        );
        byId("sign-in").hidden = response.status !== 401;
    }
    // Error bodies are not presentation content. Only known status/field names
    // select fixed client messages; raw provider/proxy output is never rendered.
    if (!response.ok) {
        const error = new Error("Request failed");
        error.status = response.status;
        if (response.status === 400) {
            try {
                error.fields = (await response.json()).errors;
            } catch {
                /* A malformed error still gets a safe explanation. */
            }
        }
        throw error;
    }
    const value = await response.json();
    if (generation !== sessionGeneration) throw new Error("Session changed");
    return value;
}

function clearAccess(text) {
    clearTimeout(sessionExpiryTimer);
    sessionExpiryTimer = null;
    ++sessionGeneration;
    ++listGeneration;
    ++detailGeneration;
    ++historyGeneration;
    ++overviewGeneration;
    ++priorityGeneration;
    clearPriority();
    clearClosureBrief();
    byId("closure-brief").hidden = true;
    byId("priority-section").hidden = true;
    message("priority-message", "");
    clearOverview();
    message("overview-message", "");
    byId("overview-counts").setAttribute("aria-busy", "false");
    canRead = canWrite = false;
    csrfToken = null;
    selectedId = currentCase = nextAfter = null;
    if (creating) uncertain = true;
    byId("protected-workspace").hidden = true;
    byId("new-case").hidden = true;
    byId("case-detail").hidden = true;
    byId("case-list").replaceChildren();
    byId("case-history").replaceChildren();
    for (const id of [
        "case-id",
        "case-title",
        "case-description",
        "mutation-message",
        "list-message",
        "detail-message",
        "history-message",
        "create-message",
    ])
        byId(id).textContent = "";
    byId("case-status").textContent = "";
    byId("case-status").removeAttribute("data-status");
    byId("create-dialog").close();
    byId("create-form").reset();
    byId("case-lookup-form").reset();
    byId("case-lookup").setAttribute("aria-invalid", "false");
    byId("case-lookup-error").textContent = "";
    byId("refresh-case").disabled = byId("refresh-history").disabled = true;
    byId("refresh-priority").disabled = true;
    byId("access-label").textContent = "ACCESS REQUIRED";
    message("access-message", text, true);
    renderAction();
    updateDraftControls();
}

async function refreshAccess() {
    if (signingOut) return;
    clearAccess("Checking session…");
    const generation = sessionGeneration;
    try {
        const session = await request("/auth/session");
        if (generation !== sessionGeneration) return;
        canRead = session.canRead === true;
        canWrite = canRead && session.canWrite === true;
        csrfToken = session.csrfToken;
        if (!session.localDemo && session.authenticated) {
            if (
                !Number.isFinite(session.expiresInSeconds) ||
                session.expiresInSeconds <= 0 ||
                session.expiresInSeconds > 1800
            )
                throw new Error("Invalid session deadline");
            sessionExpiryTimer = setTimeout(() => {
                clearAccess(
                    "Your session has expired. Sign in to continue. Signing in loses any unfinished retry context; reconcile saved cases before creating again.",
                );
                byId("sign-in").hidden = false;
            }, session.expiresInSeconds * 1000);
        }
        byId("sign-in").hidden = session.localDemo || session.authenticated;
        byId("sign-out").hidden = session.localDemo || !session.authenticated;
        byId("new-case").hidden = !canWrite;
        byId("protected-workspace").hidden = !canRead;
        byId("access-label").textContent = session.localDemo
            ? "LOCAL DEMO"
            : canWrite
              ? "CASEWORKER"
              : canRead
                ? "READER"
                : "ACCESS REQUIRED";
        message(
            "access-message",
            canRead
                ? session.localDemo
                    ? "Loopback demonstration · No sign-in required"
                    : "Signed in · Sessions expire after 30 minutes. Use Refresh access to check your session."
                : session.authenticated
                  ? "Access denied. Ask the operator for a Reader or Caseworker grant, then sign out and sign in again."
                  : "Sign in to view cases. Signing in navigates away and loses any unfinished draft or retry context.",
        );
        if (canRead) {
            void loadList();
            void loadOverview();
        }
        updateDraftControls();
    } catch {
        if (generation === sessionGeneration) {
            clearAccess(
                "Session could not be checked. Use Refresh access to retry.",
            );
            message(
                "access-message",
                "Session could not be checked. Use Refresh access to retry.",
                true,
            );
        }
    }
}

async function signOut() {
    if (signingOut) return;
    if (
        (creating || uncertain) &&
        !window.confirm(
            "This creation may already have committed. Signing out loses its retry context. Sign out and reconcile saved cases before creating again?",
        )
    )
        return;
    signingOut = true;
    byId("refresh-access").disabled = byId("sign-out").disabled = true;
    // Hide protected content immediately and discard every earlier response.
    const token = csrfToken;
    clearAccess("Signing out…");
    csrfToken = token;
    try {
        await request("/auth/logout", { method: "POST" });
        draft = null;
        uncertain = false;
        csrfToken = null;
        byId("sign-in").hidden = false;
        byId("sign-out").hidden = true;
        message(
            "access-message",
            "Signed out of this application. Your identity-provider session may still be active.",
        );
    } catch {
        csrfToken = null;
        message(
            "access-message",
            "Sign-out could not be confirmed. Protected content is hidden. Refresh access before trying again.",
            true,
        );
    } finally {
        signingOut = false;
        byId("refresh-access").disabled = byId("sign-out").disabled = false;
    }
}

function caseValue(value) {
    if (
        !value ||
        !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value.id) ||
        typeof value.title !== "string" ||
        typeof value.description !== "string" ||
        !Object.hasOwn(statusNames, value.status)
    )
        throw new Error("Invalid case response");
    return value;
}

function markSelection() {
    for (const button of byId("case-list").querySelectorAll("button")) {
        button.setAttribute(
            "aria-pressed",
            String(button.dataset.id === selectedId),
        );
    }
}

function searchContext() {
    byId("search-context").textContent =
        byId("title-search").value !== (submittedTitle ?? "")
            ? "Text edited. Choose Search to apply it; paging and Refresh keep the last submitted search."
            : submittedTitle === null
              ? "Showing all titles."
              : "Using the submitted title search.";
}

async function loadList(after = null) {
    if (!canRead) return;
    const generation = ++listGeneration;
    const filter = byId("status-filter").value;
    const query = new URLSearchParams();
    if (filter) query.set("status", filter);
    const priority = byId("priority-filter").value;
    if (priority) query.set("priority", priority);
    if (submittedTitle !== null) query.set("title", submittedTitle);
    if (after) query.set("after", after);
    byId("search-error").textContent = "";
    byId("title-search").setAttribute("aria-invalid", "false");
    nextAfter = null;
    byId("next-page").hidden = true;
    byId("case-list").replaceChildren();
    byId("case-list").setAttribute("aria-busy", "true");
    message("list-message", "Loading cases…");
    try {
        const page = await request("/cases" + (query.size ? "?" + query : ""));
        if (generation !== listGeneration) return;
        if (
            !Array.isArray(page.items) ||
            page.items.length > 20 ||
            !(page.nextAfter === null || typeof page.nextAfter === "string")
        )
            throw new Error("Invalid page");
        const cases = page.items.map(caseValue);
        for (const record of cases) {
            const button = node("button");
            button.type = "button";
            button.dataset.id = record.id;
            button.append(node("span", record.title), badge(record.status));
            button.addEventListener("click", () => selectCase(record.id));
            const item = node("li");
            item.append(button);
            byId("case-list").append(item);
        }
        markSelection();
        searchContext();
        nextAfter = page.nextAfter;
        byId("next-page").hidden = nextAfter === null;
        message(
            "list-message",
            cases.length
                ? ""
                : "No cases match this view. Clear or change the search/status/priority, or create a synthetic case.",
        );
    } catch (error) {
        if (generation === listGeneration) {
            if (
                error.status === 400 &&
                Object.hasOwn(error.fields || {}, "title")
            ) {
                byId("search-error").textContent =
                    "Use 1–120 characters of valid title text after trimming, or Clear search.";
                byId("title-search").setAttribute("aria-invalid", "true");
                byId("search-context").textContent =
                    "The submitted title search was rejected. Correct it and choose Search, or Clear search.";
            }
            message(
                "list-message",
                error.status === 400
                    ? "The search request was rejected. Check the title search before retrying."
                    : "Cases could not be loaded. Use Refresh list to try again.",
                true,
            );
        }
    } finally {
        if (generation === listGeneration)
            byId("case-list").setAttribute("aria-busy", "false");
    }
}

function clearOverview() {
    byId("overview-counts").hidden = true;
    for (const field of ["new", "inReview", "closed", "total"])
        byId("overview-" + field).textContent = "";
}

async function loadOverview() {
    if (!canRead) return;
    const generation = ++overviewGeneration;
    clearOverview();
    byId("overview-counts").setAttribute("aria-busy", "true");
    message("overview-message", "Loading workload totals…");
    try {
        const summary = await request("/cases/summary");
        if (generation !== overviewGeneration) return;
        const fields = ["new", "inReview", "closed", "total"];
        if (
            !summary ||
            fields.some(
                (field) =>
                    !Number.isSafeInteger(summary[field]) || summary[field] < 0,
            ) ||
            summary.new + summary.inReview + summary.closed !== summary.total
        )
            throw new Error("Invalid workload totals");
        for (const field of fields)
            byId("overview-" + field).textContent = String(summary[field]);
        byId("overview-counts").hidden = false;
        message("overview-message", "");
    } catch {
        if (generation === overviewGeneration)
            message(
                "overview-message",
                "Workload totals could not be loaded. Use Refresh totals to try again.",
                true,
            );
    } finally {
        if (generation === overviewGeneration)
            byId("overview-counts").setAttribute("aria-busy", "false");
    }
}

function renderAction() {
    const button = byId("advance-case");
    button.hidden =
        !canWrite || !currentCase || currentCase.status === "Closed";
    button.disabled = changingStatus || changingPriority || currentPriority === null;
    button.textContent = changingStatus
        ? "Saving…"
        : currentCase?.status === "New"
          ? "Start review"
          : "Close case";
    byId("closed-note").hidden = currentCase?.status !== "Closed";
    renderPriority();
    byId("view-closure-brief").disabled = !canRead || !selectedId || changingStatus;
}

async function loadDetails(id) {
    const generation = ++detailGeneration;
    currentCase = null;
    byId("case-detail").hidden = true;
    renderAction();
    message("detail-message", "Loading case details…");
    try {
        const record = caseValue(await request("/cases/" + id));
        if (generation !== detailGeneration || id !== selectedId) return;
        if (record.id !== id) throw new Error("Unexpected case ID");
        currentCase = record;
        byId("case-status").replaceWith(
            Object.assign(badge(record.status), { id: "case-status", tabIndex: -1 }),
        );
        byId("case-id").textContent = record.id;
        byId("case-title").textContent = record.title;
        byId("case-description").textContent = record.description;
        renderAction();
        byId("case-detail").hidden = false;
        message("detail-message", "");
    } catch (error) {
        if (generation === detailGeneration && id === selectedId) {
            message(
                "detail-message",
                error.status === 404
                    ? "This case could not be found. Check the ID or choose a case from the list."
                    : "Case details could not be loaded. Use Refresh case to try again.",
                true,
            );
        }
    }
}

function clearPriority() {
    currentPriority = null;
    byId("case-priority").textContent = "";
    byId("priority-value").hidden = true;
    byId("priority-form").hidden = true;
    byId("priority-choice").selectedIndex = -1;
    byId("priority-section").setAttribute("aria-busy", "false");
}

function priorityValue(value, id) {
    if (
        !value ||
        value.caseId !== id ||
        !priorityNames.includes(value.priority) ||
        Object.keys(value).sort().join(",") !== "caseId,priority"
    )
        throw new Error("Invalid priority response");
    return value.priority;
}

function renderPriority() {
    const pending = changingStatus || changingPriority;
    byId("priority-form").hidden =
        !canWrite || !currentCase || currentPriority === null ||
        currentCase.status === "Closed";
    byId("priority-choice").disabled = pending;
    byId("save-priority").disabled =
        pending || currentPriority === null ||
        byId("priority-choice").value === currentPriority;
    byId("save-priority").textContent = changingPriority ? "Saving…" : "Save priority";
    byId("refresh-priority").disabled = !canRead || !selectedId || pending;
}

async function loadPriority(id) {
    if (!canRead) return;
    const generation = ++priorityGeneration;
    clearPriority();
    renderAction();
    byId("priority-section").setAttribute("aria-busy", "true");
    message("priority-message", "Loading case priority…");
    try {
        const priority = priorityValue(
            await request("/cases/" + id + "/priority"), id,
        );
        if (generation !== priorityGeneration || id !== selectedId) return;
        currentPriority = priority;
        byId("case-priority").textContent = priority;
        byId("priority-value").hidden = false;
        byId("priority-choice").value = priority;
        message("priority-message", "");
    } catch (error) {
        if (generation === priorityGeneration && id === selectedId)
            message(
                "priority-message",
                error.status === 404
                    ? "This case could not be found. Check the ID or choose a case from the list."
                    : "Case priority could not be loaded. Use Refresh priority to try again.",
                true,
            );
    } finally {
        if (generation === priorityGeneration && id === selectedId) {
            byId("priority-section").setAttribute("aria-busy", "false");
            renderAction();
        }
    }
}

function requireCurrentState(id) {
    if (id !== selectedId) return;
    ++detailGeneration;
    ++priorityGeneration;
    currentCase = null;
    byId("case-detail").hidden = true;
    clearClosureBrief();
    clearPriority();
    message("detail-message", "Refresh case to read current state before another action.", true);
    message("priority-message", "Refresh case and priority before another action.", true);
    renderAction();
}

async function savePriority(event) {
    event.preventDefault();
    const priority = byId("priority-choice").value;
    if (
        !canWrite || !currentCase || currentPriority === null ||
        changingStatus || changingPriority || currentCase.status === "Closed" ||
        !priorityNames.includes(priority) || priority === currentPriority
    ) return;
    const generation = sessionGeneration;
    const id = currentCase.id;
    const title = currentCase.title;
    changingPriority = true;
    renderAction();
    message("mutation-message", "Saving priority change…");
    try {
        const value = await request("/cases/" + id + "/priority", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ priority }),
        });
        if (priorityValue(value, id) !== priority) throw new Error("Unconfirmed priority");
        message(
            "mutation-message",
            `Priority change confirmed for “${title}”. Refreshing current views; any read failure below does not undo this change.`,
        );
        if (id === selectedId) selectCase(id);
        void loadList();
        void loadOverview();
    } catch (error) {
        if (generation !== sessionGeneration) return;
        message(
            "mutation-message",
            error.status === 409
                ? "This case is closed and the priority change was rejected. Refresh case before choosing another action."
                : "The priority change could not be confirmed. Refresh case and priority to reconcile before choosing another action.",
            true,
        );
        requireCurrentState(id);
    } finally {
        // Access may have refreshed while this write was in flight. Even an ignored
        // old-session result can leave a newly selected copy of this case stale.
        if (generation !== sessionGeneration) requireCurrentState(id);
        changingPriority = false;
        renderAction();
    }
}

async function loadHistory(id) {
    const generation = ++historyGeneration;
    byId("case-history").replaceChildren();
    message("history-message", "Loading recorded history…");
    try {
        const history = await request("/cases/" + id + "/history");
        if (generation !== historyGeneration || id !== selectedId) return;
        if (
            !Array.isArray(history.items) ||
            history.items.length < 1 ||
            history.items.length > 3
        )
            throw new Error("Invalid history");
        const entries = [...history.items].sort(
            (a, b) => a.sequence - b.sequence,
        );
        const rendered = entries.map((entry, index) => {
            if (
                entry.sequence !== index + 1 ||
                !Object.hasOwn(statusNames, entry.toStatus) ||
                (entry.kind === "StatusChanged" && !Object.hasOwn(statusNames, entry.fromStatus)) ||
                typeof entry.recordedAtUtc !== "string" ||
                !["Created", "Baseline", "StatusChanged"].includes(entry.kind)
            )
                throw new Error("Invalid history");
            const label =
                entry.kind === "Baseline"
                    ? "State first recorded at upgrade: " +
                      statusNames[entry.toStatus]
                    : entry.kind === "Created"
                      ? "Case created · New"
                      : statusNames[entry.fromStatus] +
                        " → " +
                        statusNames[entry.toStatus];
            const item = node("li", label);
            item.id = "case-history-entry-" + entry.sequence;
            item.tabIndex = -1;
            item.dataset.transition = entry.kind === "StatusChanged"
                ? entry.fromStatus + ":" + entry.toStatus : "";
            const time = node("time", entry.recordedAtUtc + " (UTC)");
            time.dateTime = entry.recordedAtUtc;
            item.append(time);
            return item;
        });
        byId("case-history").replaceChildren(...rendered);
        message("history-message", "");
    } catch {
        if (generation === historyGeneration && id === selectedId)
            message(
                "history-message",
                "Recorded history could not be loaded. Use Refresh history to try again.",
                true,
            );
    }
}

function clearClosureBrief() {
    ++briefGeneration;
    byId("closure-brief-facts").replaceChildren();
    byId("closure-brief-facts").hidden = true;
    byId("closure-brief-facts").setAttribute("aria-busy", "false");
    message("closure-brief-message", "");
}

function closureBriefFacts(value, id) {
    if (value?.caseId !== id || !Array.isArray(value.facts))
        throw new Error("Invalid closure brief");
    if (value.outcome === "abstain" && value.facts.length === 0) {
        if (value.reason === "case_not_closed") return "This case is open. A closure brief is available after a recorded close.";
        if (value.reason === "closure_transition_unrecorded") return "This case was first recorded as Closed at upgrade. No closure transition or closure time was recorded.";
    }
    if (value.outcome !== "draft" || value.reason !== null || value.facts.length !== 3)
        throw new Error("Invalid closure brief");

    const facts = new Map(value.facts.map(fact => [fact.name, fact]));
    const status = facts.get("currentStatus");
    const previous = facts.get("previousStatus");
    const time = facts.get("closedAtUtc");
    const sequence = ["2", "3"].find(sequence => time?.citation === `history:${id}#${sequence}`);
    if (!sequence || status?.value !== "Closed" || status.citation !== `case:${id}#status` ||
        previous?.value !== "InReview" || previous.citation !== time.citation ||
        typeof time.value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$/.test(time.value))
        throw new Error("Invalid closure brief");

    const historySource = () => {
        const entry = byId("case-history-entry-" + sequence);
        return entry?.dataset.transition === "InReview:Closed" &&
            entry.querySelector("time")?.dateTime === time.value ? entry : null;
    };
    const caseSource = () => currentCase?.id === id && currentCase.status === "Closed"
        ? byId("case-status") : null;
    return [
        ["Current status", "Closed", "Case status", "", caseSource],
        ["Previous status", "In review", "History entry " + sequence, "/history", historySource],
        ["Recorded closure time", time.value + " (UTC)", "History entry " + sequence, "/history", historySource],
    ].flatMap(([label, text, source, path, findSource]) => {
        const link = node("a", source);
        link.href = "/cases/" + id + path;
        link.addEventListener("click", event => {
            if (selectedId !== id) {
                event.preventDefault();
                return;
            }
            if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
            const target = findSource();
            if (!target) return;
            event.preventDefault();
            target.focus();
        });
        const definition = node("dd", text + " · ");
        definition.append(link);
        return [node("dt", label), definition];
    });
}

async function viewClosureBrief() {
    if (!canRead || !selectedId || changingStatus) return;
    const id = selectedId;
    clearClosureBrief();
    const generation = briefGeneration;
    const facts = byId("closure-brief-facts");
    facts.setAttribute("aria-busy", "true");
    message("closure-brief-message", "Loading closure brief…");
    try {
        const response = await request("/cases/" + id + "/closure-brief");
        if (generation !== briefGeneration || id !== selectedId) return;
        const result = closureBriefFacts(response, id);
        if (typeof result === "string") message("closure-brief-message", result);
        else {
            facts.replaceChildren(...result);
            facts.hidden = false;
            message("closure-brief-message", "Closure brief ready for review.");
        }
    } catch {
        if (generation === briefGeneration && id === selectedId)
            message("closure-brief-message", "The closure brief could not be verified. Check the case and recorded history, then try again.", true);
    } finally {
        if (generation === briefGeneration) facts.setAttribute("aria-busy", "false");
    }
}

function selectCase(id) {
    if (!canRead) return;
    selectedId = id;
    clearClosureBrief();
    byId("closure-brief").hidden = false;
    byId("case-lookup").value = id;
    byId("case-lookup").setAttribute("aria-invalid", "false");
    byId("case-lookup-error").textContent = "";
    markSelection();
    byId("refresh-case").disabled = false;
    byId("refresh-history").disabled = false;
    void loadDetails(id);
    void loadHistory(id);
    byId("priority-section").hidden = false;
    void loadPriority(id);
}

async function advanceCase() {
    if (
        !canWrite ||
        !currentCase ||
        changingStatus || changingPriority || currentPriority === null ||
        currentCase.status === "Closed"
    )
        return;
    const generation = sessionGeneration;
    const id = currentCase.id;
    const title = currentCase.title;
    const status = currentCase.status === "New" ? "InReview" : "Closed";
    changingStatus = true;
    clearClosureBrief();
    renderAction();
    message("mutation-message", "Saving status change…");
    try {
        await request("/cases/" + id + "/status", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ status }),
        });
        message(
            "mutation-message",
            `Status change confirmed for “${title}”. Refreshing current views; any read failure below does not undo this change.`,
        );
        if (id === selectedId) selectCase(id);
        void loadList();
        void loadOverview();
    } catch (error) {
        if (generation !== sessionGeneration) return;
        message(
            "mutation-message",
            error.status === 409
                ? "The case has changed and this action was rejected. Refresh the case before choosing another action."
                : "The status change could not be confirmed. Refresh case, priority and history to reconcile before trying again.",
            true,
        );
        // Status and priority both need fresh observations after an uncertain write.
        requireCurrentState(id);
    } finally {
        if (generation !== sessionGeneration) requireCurrentState(id);
        changingStatus = false;
        renderAction();
    }
}

function updateDraftControls() {
    byId("title").readOnly = creating || uncertain;
    byId("description").readOnly = creating || uncertain;
    byId("new-case").hidden = !canWrite;
    byId("create-submit").disabled = creating || !canWrite;
    byId("create-submit").textContent = creating
        ? "Creating…"
        : uncertain
          ? "Retry same draft"
          : "Create case";
    byId("close-draft").disabled = creating;
    byId("uncertain-note").hidden = !uncertain;
    byId("request-new-draft").hidden = !uncertain;
    byId("request-new-draft").disabled = creating;
    byId("replace-draft").disabled =
        creating || !byId("acknowledge-new").checked;
    byId("new-case").textContent = uncertain ? "Resume draft" : "+ New case";
}

function fieldError(field, text) {
    byId(field + "-error").textContent = text;
    byId(field).setAttribute("aria-invalid", String(Boolean(text)));
}

async function createCase(event) {
    event.preventDefault();
    if (creating || !canWrite) return;
    const generation = sessionGeneration;
    fieldError("title", "");
    fieldError("description", "");
    if (!uncertain) {
        const title = byId("title").value;
        const description = byId("description").value;
        if (!title.trim() || title.trim().length > 120)
            fieldError("title", "Use 1–120 characters after trimming.");
        if (!description.trim() || description.trim().length > 2000)
            fieldError("description", "Use 1–2,000 characters after trimming.");
        const invalid = byId("create-form").querySelector(
            '[aria-invalid="true"]',
        );
        if (invalid) {
            invalid.focus();
            return;
        }
        draft = {
            key: '"' + crypto.randomUUID() + '"',
            payload: JSON.stringify({ title, description }),
        };
    }
    const wasUncertain = uncertain;
    creating = true;
    updateDraftControls();
    message("create-message", "Submitting case…");
    try {
        const record = caseValue(
            await request("/cases", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Idempotency-Key": draft.key,
                },
                body: draft.payload,
            }),
        );
        uncertain = false;
        draft = null;
        byId("create-form").reset();
        byId("replacement").hidden = true;
        message("create-message", "");
        byId("create-dialog").close();
        message(
            "mutation-message",
            "Creation confirmed. Loading current case state; any read failure below does not undo creation.",
        );
        selectCase(record.id);
        void loadList();
        void loadOverview();
    } catch (error) {
        if (generation !== sessionGeneration) {
            uncertain = draft !== null;
            return;
        }
        if (error.status === 400 && !wasUncertain) {
            draft = null;
            for (const field of ["title", "description"]) {
                if (Object.hasOwn(error.fields || {}, field))
                    fieldError(
                        field,
                        field === "title"
                            ? "Use 1–120 characters after trimming."
                            : "Use 1–2,000 characters after trimming.",
                    );
            }
            message(
                "create-message",
                "The request was rejected. Check the fields and try again.",
                true,
            );
        } else {
            uncertain = true;
            message(
                "create-message",
                error.status === 422
                    ? "This retry key is already bound to different fields. It has not been replaced. Review the earlier outcome before starting another draft."
                    : "Creation could not be confirmed. Retry this same draft to reconcile safely.",
                true,
            );
        }
    } finally {
        creating = false;
        updateDraftControls();
    }
}

byId("case-lookup-form").addEventListener("submit", (event) => {
    event.preventDefault();
    if (!canRead) return;
    const id = byId("case-lookup").value.trim().toLowerCase();
    if (
        !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(id) ||
        id === "00000000-0000-0000-0000-000000000000"
    ) {
        byId("case-lookup").setAttribute("aria-invalid", "true");
        byId("case-lookup-error").textContent =
            "Use a non-empty case ID: 32 hexadecimal characters with hyphens in the 8-4-4-4-12 format.";
        byId("case-lookup").focus();
        return;
    }
    selectCase(id);
});
byId("search-form").addEventListener("submit", (event) => {
    event.preventDefault();
    submittedTitle = byId("title-search").value || null;
    searchContext();
    void loadList();
});
byId("title-search").addEventListener("input", searchContext);
byId("clear-search").addEventListener("click", () => {
    submittedTitle = null;
    byId("title-search").value = "";
    searchContext();
    void loadList();
});
byId("status-filter").addEventListener("change", () => void loadList());
byId("priority-filter").addEventListener("change", () => void loadList());
byId("priority-choice").addEventListener("change", renderPriority);
byId("priority-form").addEventListener("submit", (event) => void savePriority(event));
byId("refresh-priority").addEventListener("click", () => {
    if (canRead && selectedId && !changingStatus && !changingPriority) void loadPriority(selectedId);
});
byId("refresh-list").addEventListener("click", () => void loadList());
byId("refresh-overview").addEventListener("click", () => void loadOverview());
byId("next-page").addEventListener("click", () => {
    if (nextAfter) void loadList(nextAfter);
});
byId("refresh-case").addEventListener("click", () => {
    if (selectedId) selectCase(selectedId);
});
byId("refresh-history").addEventListener("click", () => {
    if (!selectedId) return;
    clearClosureBrief();
    void loadHistory(selectedId);
});
byId("view-closure-brief").addEventListener("click", () => void viewClosureBrief());
byId("advance-case").addEventListener("click", () => void advanceCase());
byId("new-case").addEventListener("click", () => {
    if (!canWrite) return;
    if (draft && uncertain) {
        const payload = JSON.parse(draft.payload);
        byId("title").value = payload.title;
        byId("description").value = payload.description;
    }
    byId("create-dialog").showModal();
});
byId("close-draft").addEventListener("click", () =>
    byId("create-dialog").close(),
);
byId("create-dialog").addEventListener("cancel", (event) => {
    if (creating) event.preventDefault();
});
byId("create-form").addEventListener(
    "submit",
    (event) => void createCase(event),
);
byId("request-new-draft").addEventListener("click", () => {
    byId("replacement").hidden = false;
});
byId("acknowledge-new").addEventListener("change", updateDraftControls);
byId("replace-draft").addEventListener("click", () => {
    if (creating || !byId("acknowledge-new").checked) return;
    draft = null;
    uncertain = false;
    byId("create-form").reset();
    byId("replacement").hidden = true;
    message(
        "create-message",
        "New draft started. The earlier request may have created a separate case.",
    );
    updateDraftControls();
    byId("title").focus();
});
window.addEventListener("beforeunload", (event) => {
    if (creating || uncertain) {
        event.preventDefault();
        event.returnValue = "";
    }
});
byId("refresh-access").addEventListener("click", () => void refreshAccess());
byId("sign-out").addEventListener("click", () => void signOut());
void refreshAccess();
