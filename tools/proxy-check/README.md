# proxy-check

A self-contained check of the launcher's store path. It stands up a fake launcher server on
127.0.0.1, points the real `ApiClient`, `CurseForgeService` and `ModrinthService` at it, and then:

1. calls every store method the launcher has and feeds each recorded `/proxy/{platform}/{path}`
   through the server's `ProxyAllowlist` (compiled in from `CloudLauncher.Server/Net`), failing on
   any path the server would refuse and on any allowlist entry the launcher never exercised;
2. checks `ProxyUserLimits` at its configured numbers (burst, sustained rate, queue places);
3. scripts 429, 503, 502 and 403 answers and checks the client waits and retries them silently,
   honours `Retry-After`, gives up after the retries with a calm sentence, and never blames an
   API key for a shared-key answer.

Nothing talks to the real stores or the real server. It uses its own launcher profile
(`CL_PROFILE=proxy-check-<pid>`), which it deletes afterwards.

```
dotnet build tools/proxy-check/ProxyCheck.csproj -nologo -v q --artifacts-path <somewhere>
<somewhere>/bin/ProxyCheck/debug/cl-proxy-check.exe
```

Exit code 0 means every check passed; the output lists each one.
