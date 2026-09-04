// Hand-written. The bridge the browser host (Subprocess/GTANetwork.CefHost, ResourceBridgeInjector) injects into every
// page it serves for a resource, and into documents given to loadHtmlCefBrowser. All calls are one-way: the page runs
// in another process, so there is no return value; a reply comes back as JavaScript the client script evaluates in the
// page (Browser.eval / Browser.call).

/** Calls the global function `name` of the client script that owns this browser, with the arguments (JSON-serialisable). */
declare function resourceCall(name: string, ...args: unknown[]): void;
/** Runs `code` in the V8 engine of the client script that owns this browser. */
declare function resourceEval(code: string): void;
declare const gtan: {
    call(name: string, ...args: unknown[]): void;
    eval(code: string): void;
};
