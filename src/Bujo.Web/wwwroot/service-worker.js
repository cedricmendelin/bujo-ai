"use strict";

// No document, navigation, API, authentication or journal responses belong here.
const CACHE_NAME = "bujo-static-shell-v1";
const STATIC_ASSETS = new Map([
    ["/_content/Bujo.Web/pwa.css", "text/css"],
    ["/_content/Bujo.Web/pwa.js", "javascript"],
    ["/_content/Bujo.Web/manifest.webmanifest", "manifest"],
    ["/_content/Bujo.Web/icons/icon-192.png", "image/png"],
    ["/_content/Bujo.Web/icons/icon-512.png", "image/png"]
]);

class StaticNetworkError extends Error {}

async function fetchStatic(url) {
    let response;
    try {
        response = await fetch(url, { credentials: "omit", cache: "no-cache", redirect: "error" });
    } catch {
        throw new StaticNetworkError("Static asset network request failed.");
    }
    const expectedType = STATIC_ASSETS.get(new URL(url).pathname);
    const actualType = response.headers.get("content-type") || "";
    const validType = expectedType === "manifest"
        ? /^(application\/manifest\+json|application\/json)(;|$)/i.test(actualType)
        : actualType.toLowerCase().includes(expectedType);
    if (!response.ok || response.type !== "basic" || !validType ||
        /(?:no-store|private)/i.test(response.headers.get("cache-control") || "")) {
        throw new Error("Static asset was not a public, expected response.");
    }
    return response;
}

self.addEventListener("install", event => {
    event.waitUntil((async () => {
        const cache = await caches.open(CACHE_NAME);
        for (const path of STATIC_ASSETS.keys()) {
            const url = new URL(path, self.location.origin).href;
            await cache.put(url, await fetchStatic(url));
        }
    })());
});

self.addEventListener("activate", event => {
    event.waitUntil((async () => {
        for (const name of await caches.keys()) {
            if (name.startsWith("bujo-static-shell-") && name !== CACHE_NAME) {
                await caches.delete(name);
            }
        }
        await self.clients.claim();
    })());
});

self.addEventListener("fetch", event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== "GET" || request.mode === "navigate" ||
        url.origin !== self.location.origin || url.search ||
        !STATIC_ASSETS.has(url.pathname) || request.headers.has("authorization") ||
        request.headers.has("range")) {
        return;
    }
    event.respondWith((async () => {
        const cache = await caches.open(CACHE_NAME);
        try {
            const response = await fetchStatic(url.href);
            await cache.put(url.href, response.clone());
            return response;
        } catch (error) {
            if (error instanceof StaticNetworkError) {
                const cached = await cache.match(url.href);
                if (cached) return cached;
            }
            throw error;
        }
    })());
});
