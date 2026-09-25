# Helper verification — 0.1.0

29 helper tests passed in this delivery environment.

Local verification uses Python standard-library unittest on Linux. These are helper
tests and mock GitHub interactions, not actual Unity/BepInEx gameplay evidence.

Coverage: exact commit downloads; unsafe ZIP rejection; report allowlist; no raw
output stored; child cancellation/timeouts; bounded output; duplicate-launch lock;
failure prevents later steps; SKIP preservation; no false gameplay PASS from exit 0;
upload branch separation; pending retry after lost response; automatic mode new
revision detection, failure stop and run cap; interrupted deployment recovery.

Not executed here: Windows batch launchers, Windows Job Objects/taskkill, interactive
GitHub CLI login, live PC report uploads, .NET build against installed game assemblies,
H2/bootstrap/gameplay, forced-window-close recovery on Windows. The release does not
claim those passed. The code is published through the connected GitHub integration.

Windows acceptance:

1. Install prerequisites; SETUP then START; complete menu 1 with the real game closed.
2. Check the report commit matches tested code, and individual H2 results are honest.
3. Verify the previously installed DLL is byte-for-byte restored after PASS/FAIL.
4. During a run, click STOP. Check the owned game/build ends, original DLL is restored,
   and no auto-restart occurs. Close/reopen START: manual mode only.
5. Test two START windows: the second must refuse to run. Try upload without write
   permission: preserve pending report, stop auto, retry via menu 3 after fixing access.
6. After a successful manual test, enable AUTO, publish one intentional code update,
   verify one test; report commits must never trigger another test.

GitHub API references used for the transport:
- https://cli.github.com/manual/gh_api
- https://docs.github.com/en/rest/repos/contents
- https://docs.github.com/en/rest/guides/using-the-rest-api-to-interact-with-your-git-database
