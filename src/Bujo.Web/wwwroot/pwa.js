(() => {
    "use strict";

    let circuitConnected = false;
    let circuitProbe;
    let connectionGeneration = 0;
    let modalWasDisconnected = false;
    const blockedElements = new Map();
    const writeSelector = "[data-journal-write]";
    const controlSelector = "button, input, select, textarea, fieldset";

    function writesAvailable() {
        return navigator.onLine && circuitConnected;
    }

    function setText(element, text) {
        if (element.textContent !== text) element.textContent = text;
    }

    function update() {
        const available = writesAvailable();
        const requiresCircuit = document.querySelector("[data-journal-write], [data-requires-circuit]") !== null;
        document.querySelectorAll("[data-connectivity-status]").forEach(element => {
            setText(element, navigator.onLine
                ? "Browser network available. Saves still require server confirmation."
                : "Offline. Writes are disabled; nothing will be queued. Reloading loses unsaved input.");
            element.dataset.offline = String(!navigator.onLine);
        });
        document.querySelectorAll("[data-circuit-status]").forEach(element => {
            const hidden = circuitConnected || !requiresCircuit;
            if (element.hidden !== hidden) element.hidden = hidden;
            setText(element, circuitConnected ? "" :
                "Server connection lost or awaiting reconnection. Writes are disabled. " +
                "Keep this page open to retain unsaved input; reloading loses it. " +
                "No writes are replayed. A missing save confirmation means the outcome is unknown.");
        });

        if (!available) {
            document.querySelectorAll(writeSelector).forEach(region => {
                const elements = [region, ...region.querySelectorAll(controlSelector)];
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
    }

    function circuitDown() {
        connectionGeneration++;
        circuitConnected = false;
        update();
    }

    function circuitUp() {
        if (!navigator.onLine) return update();
        connectionGeneration++;
        circuitConnected = true;
        update();
    }

    async function checkConnection() {
        update();
        if (!navigator.onLine || !circuitProbe) return;
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
            const disconnected = ["components-reconnect-show", "components-reconnect-failed",
                "components-reconnect-rejected"].some(name => modal.classList.contains(name));
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
        circuitDown,
        circuitUp,
        registerCircuitProbe: reference => {
            circuitProbe = reference;
            circuitUp();
        }
    });

    window.addEventListener("offline", circuitDown);
    window.addEventListener("online", checkConnection);
    for (const name of ["click", "submit", "keydown", "beforeinput"]) {
        document.addEventListener(name, blockDisconnectedWrite, true);
    }

    function start() {
        if (!navigator.onLine) circuitConnected = false;
        inspectReconnectModal();
        new MutationObserver(inspectReconnectModal).observe(document.documentElement, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ["class"]
        });
        if ("serviceWorker" in navigator && window.isSecureContext) {
            navigator.serviceWorker.register("/service-worker.js", { scope: "/", updateViaCache: "none" })
                .catch(() => {
                    document.querySelectorAll("[data-connectivity-status]").forEach(element => {
                        setText(element, element.textContent +
                            " App installation assets are unavailable; the online app can still be used.");
                    });
                });
        }
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
    else start();
})();
