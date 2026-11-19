const enterHandlers = new WeakMap();

export function attachEnterToSend(textarea) {
    if (enterHandlers.has(textarea)) return;

    const handler = event => {
        if (event.key !== "Enter" || event.shiftKey || event.isComposing || event.keyCode === 229) return;

        event.preventDefault();
        if (event.repeat || textarea.disabled || !textarea.value.trim()) return;

        textarea.form?.requestSubmit();
    };

    textarea.addEventListener("keydown", handler);
    enterHandlers.set(textarea, handler);
}

export function detachEnterToSend(textarea) {
    const handler = enterHandlers.get(textarea);
    if (!handler) return;

    textarea.removeEventListener("keydown", handler);
    enterHandlers.delete(textarea);
}
