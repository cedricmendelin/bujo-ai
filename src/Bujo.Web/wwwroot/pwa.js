(() => {
    "use strict";

    let circuitConnected = navigator.onLine;
    let circuitProbe;
    let connectionGeneration = 0;
    let modalWasDisconnected = false;
    const blockedElements = new Map();
    const writeSelector = "[data-journal-write]";
    const controlSelector = "button, input, select, textarea, fieldset";
    const disconnectedClasses = ["show", "retrying", "failed", "rejected", "paused", "resume-failed"]
        .map(state => `components-reconnect-${state}`);

    function writesAvailable() {
        return navigator.onLine && circuitConnected;
    }

    function update() {
        const available = writesAvailable();

        if (!available) {
            document.querySelectorAll(writeSelector).forEach(region => {
                // Journal forms own their controls' disabled state in Blazor. Block the
                // whole region without overwriting disabled state during a render.
                const elements = region.hasAttribute("data-journal-form")
                    ? [region] : [region, ...region.querySelectorAll(controlSelector)];
                for (const element of elements) {
                    if (blockedElements.has(element)) continue;
                    blockedElements.set(element, {
                        disabled: "disabled" in element ? element.disabled : null,
                        inert: element.inert,
                        ariaDisabled: element.getAttribute("aria-disabled")
                    });
                    if ("disabled" in element) element.disabled = true;
                    element.inert = true;
                    element.setAttribute("aria-disabled", "true");
                }
            });
        } else {
            for (const [element, previous] of blockedElements) {
                if (previous.disabled !== null) element.disabled = previous.disabled;
                element.inert = previous.inert;
                if (previous.ariaDisabled === null) element.removeAttribute("aria-disabled");
                else element.setAttribute("aria-disabled", previous.ariaDisabled);
            }
            blockedElements.clear();
        }
        for (const element of blockedElements.keys()) {
            if (!element.isConnected) blockedElements.delete(element);
        }
    }

    function circuitDown() {
        connectionGeneration++;
        circuitConnected = false;
        update();
    }

    function circuitUp() {
        connectionGeneration++;
        circuitConnected = true;
        update();
    }

    async function checkConnection() {
        update();
        if (writesAvailable() || !navigator.onLine || !circuitProbe) return;
        const generation = connectionGeneration;
        try {
            // Read-only round trip: this never submits or replays journal changes.
            const confirmed = await circuitProbe.invokeMethodAsync("CheckConnection");
            if (confirmed && generation === connectionGeneration && navigator.onLine) circuitUp();
        } catch {
            // The framework's normal reconnect UI/retry continues to own recovery.
        }
    }

    function inspectReconnectModal() {
        const modal = document.getElementById("components-reconnect-modal");
        if (modal) {
            const disconnected = disconnectedClasses.some(name => modal.classList.contains(name));
            if (disconnected) {
                if (!modalWasDisconnected) {
                    modalWasDisconnected = true;
                    circuitDown();
                }
            } else if (modalWasDisconnected && modal.classList.contains("components-reconnect-hide")) {
                modalWasDisconnected = false;
                circuitUp();
            }
        }
        update();
    }

    function blockDisconnectedWrite(event) {
        if (event.type === "keydown" && event.key !== "Enter" && event.key !== " ") return;
        if (!writesAvailable() && event.target instanceof Element && event.target.closest(writeSelector)) {
            event.preventDefault();
            event.stopImmediatePropagation();
            update();
        }
    }

    window.bujoPwa = Object.freeze({
        getTimeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,
        writesAvailable,
        confirmKnowledgeRestore: () => window.confirm("Restoring this backup replaces the journal and saved knowledge. Continue?"),
        downloadText: (fileName, content) => {
            const url = URL.createObjectURL(new Blob([content], { type: "application/json" }));
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            link.click();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        },
        registerCircuitProbe: reference => {
            circuitProbe = reference;
            void checkConnection();
        }
    });

    window.addEventListener("offline", circuitDown);
    window.addEventListener("online", checkConnection);
    document.addEventListener("click", event => {
        if (event.target instanceof Element && event.target.closest("[data-reload-page]")) location.reload();
    });
    document.addEventListener("components-reconnect-state-changed", event => {
        if (event.target === document.getElementById("components-reconnect-modal")) inspectReconnectModal();
    }, true);
    for (const name of ["click", "submit", "keydown", "beforeinput"]) {
        document.addEventListener(name, blockDisconnectedWrite, true);
    }

    function start() {
        inspectReconnectModal();
        new MutationObserver(inspectReconnectModal).observe(document.documentElement, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ["class"]
        });
        if ("serviceWorker" in navigator && window.isSecureContext) {
            navigator.serviceWorker.register("/service-worker.js", { scope: "/", updateViaCache: "none" })
                .catch(() => { /* Installation is optional for online use. */ });
        }
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
    else start();
})();
