let recognizer = null;
let dotNetRef = null;
let finalSubmitted = false;
let listening = false;

function releaseRecognizer() {
    if (!recognizer) return;
    const previous = recognizer;
    recognizer = null;
    listening = false;
    try {
        previous.onresult = null;
        previous.onerror = null;
        previous.onend = null;
        previous.stop();
    } catch {
        // The browser may already have stopped or released the recognizer.
    }
}

export function startRecognition(reference, language) {
    if (listening) {
        stopRecognition();
    }

    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Recognition) {
        throw new Error("Speech recognition is unsupported by this browser.");
    }

    dotNetRef = reference;
    finalSubmitted = false;
    recognizer = new Recognition();
    recognizer.lang = language === "en-US" ? "en-US" : "en-US";
    recognizer.continuous = false;
    recognizer.interimResults = true;
    recognizer.maxAlternatives = 1;
    listening = true;

    recognizer.onresult = event => {
        if (finalSubmitted) return;
        for (let index = event.resultIndex; index < event.results.length; index++) {
            const result = event.results[index];
            if (!result.isFinal) continue;
            const transcript = result[0]?.transcript?.trim();
            if (!transcript) continue;
            finalSubmitted = true;
            const owner = dotNetRef;
            releaseRecognizer();
            if (owner) {
                void owner.invokeMethodAsync("ReceiveSpeechResult", transcript);
            }
            return;
        }
    };

    recognizer.onerror = event => {
        const code = event.error || "unknown";
        const owner = dotNetRef;
        releaseRecognizer();
        if (owner) {
            void owner.invokeMethodAsync("ReceiveSpeechError", code);
        }
    };

    recognizer.onend = () => {
        const owner = dotNetRef;
        releaseRecognizer();
        if (owner) {
            void owner.invokeMethodAsync("ReceiveSpeechEnded");
        }
    };

    recognizer.start();
}

export function stopRecognition() {
    releaseRecognizer();
    if (dotNetRef) {
        void dotNetRef.invokeMethodAsync("ReceiveSpeechEnded");
    }
}

export function disposeRecognition() {
    releaseRecognizer();
    dotNetRef = null;
}
