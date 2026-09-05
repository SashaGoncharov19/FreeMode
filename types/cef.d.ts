// Hand-written. The bridge the browser host (Subprocess/GTANetwork.CefHost, ResourceBridgeInjector) injects into every
// page it serves for a resource, and into documents given to loadHtmlCefBrowser. resourceCall/resourceEval/gtan.call/gtan.eval
// are one-way: the page runs in another process, a reply comes back as JavaScript the client script evaluates in the page
// (Browser.eval / Browser.call). gtan.rpc.call (T-008) is request/response: it resolves with the handler's return value.

/** Calls the global function `name` of the client script that owns this browser, with the arguments (JSON-serialisable). */
declare function resourceCall(name: string, ...args: unknown[]): void;
/** Runs `code` in the V8 engine of the client script that owns this browser. */
declare function resourceEval(code: string): void;
/** The error a rejected gtan.rpc.call carries: `code` is timeout | denied | unknown | rate | handler | size | invalid | disconnected. */
interface RpcError extends Error { code: string; }
declare const gtan: {
    call(name: string, ...args: unknown[]): void;
    eval(code: string): void;
    rpc: {
        /**
         * Calls `name`: a handler the owning client script registered with API.rpc.register, otherwise the server's handler of
         * that name (API.registerRpc / gtan.rpc.register), through the client script. `args` is one JSON-serialisable value.
         * Rejects with an RpcError. Default timeout 10 s, at most 60 s.
         */
        call<T = unknown>(name: string, args?: unknown, timeoutMs?: number): Promise<T>;
    };
};
