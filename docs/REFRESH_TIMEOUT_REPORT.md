# Incomplete HTTP response could stall refresh — 1.0.76

On 2026-10-19 the local log recorded successful Checkbox response headers at 12:47 Kyiv time, without completing that receipt snapshot. Prom and Rozetka snapshots completed at approximately the same time. No later completed receipt refresh was stored before the application was closed at 16:21. This is consistent with the body-read defect reproduced below; the log does not prove a specific remote network fault.

Checkbox's API and authentication clients used `ResponseHeadersRead`, then consumed the response body with the caller token only. `HttpClient.Timeout` had already ended after the headers. Marketplace JSON streaming had the same gap. A stalled body could hold the receipt refresh gate and the parallel receipt-settlement barrier indefinitely. Shipping transport already owns a deadline through the complete body read and does not need this change.

Checkbox API and sign-in now buffer their existing content within `SendAsync` using `ResponseContentRead`, keeping the configured timeout active until the response is complete. A request timeout becomes an `ApiException` with HTTP 408; caller cancellation remains cancellation. An incomplete image or JSON payload is never returned. Authentication releases its lock and saves no credentials after timeout.

Prom and Rozetka retain streaming and the existing 16 MiB bound. A linked per-request deadline now covers headers and every body read. A body timeout ends that request without an inline retry; the existing account cooldown controls later attempts. The separate server `Retry-After` wait uses the caller token and is not shortened by the expired request deadline.

The same log exposed a separate shutdown defect: `ExitFromTrayAsync` disposed the coordinator before `OnExit` called `Stop` again. `Stop` is now idempotent after disposal under the existing coordinator lock. The unhandled-error handler logs shutdown errors without opening another dialog, and safely handles the absence of a main window.

Nine synthetic regression groups cover stalled JSON, PNG and sign-in content, both marketplace readers, explicit cancellation, independent parallel provider completion and recovery, server backoff, and repeated shutdown. Seven failed before the fix; the explicit-cancellation and backoff guards passed. All nine pass after the fix. These fixtures send no real network requests and create no print jobs.

A separate single-read live check on 2026-10-19 returned HTTP 200 from Checkbox, Prom, Rozetka and the explicitly bound Nova Poshta account, each in less than one second. Only normal read routes and one Checkbox sign-in were used. No fiscal PDF, label download, printer submission or profile save was performed; response payloads, credentials and customer data are absent from the report.

Development Release validation passed **741/741** tests, including all nine new groups and the existing full-refresh/date-range/parallel-provider scenarios. The compiled WPF application builds successfully; the existing WFAC010 warning remains.
