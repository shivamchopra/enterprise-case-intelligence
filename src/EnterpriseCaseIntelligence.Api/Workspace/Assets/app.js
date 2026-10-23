"use strict";

const byId = (id) => document.getElementById(id);
const statusNames = { New: "New", InReview: "In review", Closed: "Closed" };
let listGeneration = 0;
let detailGeneration = 0;
let historyGeneration = 0;
let nextAfter = null;
let selectedId = null;
let currentCase = null;
let changingStatus = false;
let draft = null;
let creating = false;
let uncertain = false;

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
    const response = await fetch(path, {
        cache: "no-store",
        credentials: "omit",
        redirect: "error",
        signal: AbortSignal.timeout(15000),
        ...options,
    });
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
    return response.json();
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

async function loadList(after = null) {
    const generation = ++listGeneration;
    const filter = byId("status-filter").value;
    const query = new URLSearchParams();
    if (filter) query.set("status", filter);
    if (after) query.set("after", after);
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
        nextAfter = page.nextAfter;
        byId("next-page").hidden = nextAfter === null;
        message(
            "list-message",
            cases.length
                ? ""
                : "No cases match this view. Create a synthetic case or choose another status.",
        );
    } catch {
        if (generation === listGeneration)
            message(
                "list-message",
                "Cases could not be loaded. Use Refresh list to try again.",
                true,
            );
    } finally {
        if (generation === listGeneration)
            byId("case-list").setAttribute("aria-busy", "false");
    }
}

function renderAction() {
    const button = byId("advance-case");
    button.hidden = !currentCase || currentCase.status === "Closed";
    button.disabled = changingStatus;
    button.textContent = changingStatus
        ? "Saving…"
        : currentCase?.status === "New"
          ? "Start review"
          : "Close case";
    byId("closed-note").hidden = currentCase?.status !== "Closed";
}

async function loadDetails(id) {
    const generation = ++detailGeneration;
    currentCase = null;
    byId("case-detail").hidden = true;
    message("detail-message", "Loading case details…");
    try {
        const record = caseValue(await request("/cases/" + id));
        if (generation !== detailGeneration || id !== selectedId) return;
        currentCase = record;
        byId("case-status").replaceWith(
            Object.assign(badge(record.status), { id: "case-status" }),
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
                    ? "This case could not be found. Refresh the list."
                    : "Case details could not be loaded. Use Refresh case to try again.",
                true,
            );
        }
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
        const rendered = entries.map((entry) => {
            if (
                !Object.hasOwn(statusNames, entry.toStatus) ||
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

function selectCase(id) {
    selectedId = id;
    markSelection();
    byId("refresh-case").disabled = false;
    byId("refresh-history").disabled = false;
    void loadDetails(id);
    void loadHistory(id);
}

async function advanceCase() {
    if (!currentCase || changingStatus || currentCase.status === "Closed")
        return;
    const id = currentCase.id;
    const title = currentCase.title;
    const status = currentCase.status === "New" ? "InReview" : "Closed";
    changingStatus = true;
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
    } catch (error) {
        message(
            "mutation-message",
            error.status === 409
                ? "The case has changed and this action was rejected. Refresh the case before choosing another action."
                : "The status change could not be confirmed. Refresh the case and history to reconcile before trying again.",
            true,
        );
        // An uncertain or rejected action must not remain available from stale data.
        if (id === selectedId) {
            ++detailGeneration;
            currentCase = null;
            byId("case-detail").hidden = true;
            message(
                "detail-message",
                "Refresh case to read current state before another action.",
                true,
            );
        }
    } finally {
        changingStatus = false;
        renderAction();
    }
}

function updateDraftControls() {
    byId("title").readOnly = creating || uncertain;
    byId("description").readOnly = creating || uncertain;
    byId("create-submit").disabled = creating;
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
    if (creating) return;
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
    } catch (error) {
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

byId("status-filter").addEventListener("change", () => void loadList());
byId("refresh-list").addEventListener("click", () => void loadList());
byId("next-page").addEventListener("click", () => {
    if (nextAfter) void loadList(nextAfter);
});
byId("refresh-case").addEventListener("click", () => {
    if (selectedId) selectCase(selectedId);
});
byId("refresh-history").addEventListener("click", () => {
    if (selectedId) void loadHistory(selectedId);
});
byId("advance-case").addEventListener("click", () => void advanceCase());
byId("new-case").addEventListener("click", () =>
    byId("create-dialog").showModal(),
);
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
void loadList();
