from pathlib import Path
import re, json, sys
root=Path(__file__).resolve().parents[1]
errors=[]; checks=[]

def ok(name, cond, detail=''):
    checks.append({'name':name,'pass':bool(cond),'detail':detail})
    if not cond: errors.append(name+(': '+detail if detail else ''))

def strip_csharp(src):
    out=[];i=0;n=len(src);state='code';verbatim=False
    while i<n:
        c=src[i];d=src[i+1] if i+1<n else ''
        if state=='code':
            if c=='/' and d=='/': state='line'; out.extend('  '); i+=2; continue
            if c=='/' and d=='*': state='block'; out.extend('  '); i+=2; continue
            if c=='@' and d=='"': state='string';verbatim=True;out.extend('  ');i+=2;continue
            if c=='"': state='string';verbatim=False;out.append(' ');i+=1;continue
            if c=="'": state='char';out.append(' ');i+=1;continue
            out.append(c);i+=1;continue
        if state=='line':
            if c=='\n': state='code';out.append('\n')
            else: out.append(' ')
            i+=1;continue
        if state=='block':
            if c=='*' and d=='/': state='code';out.extend('  ');i+=2
            else: out.append('\n' if c=='\n' else ' ');i+=1
            continue
        if state=='string':
            if verbatim:
                if c=='"' and d=='"':out.extend('  ');i+=2;continue
                if c=='"':state='code';out.append(' ');i+=1;continue
            else:
                if c=='\\':out.extend('  ');i+=2;continue
                if c=='"':state='code';out.append(' ');i+=1;continue
            out.append('\n' if c=='\n' else ' ');i+=1;continue
        if state=='char':
            if c=='\\':out.extend('  ');i+=2;continue
            if c=="'":state='code';out.append(' ');i+=1;continue
            out.append('\n' if c=='\n' else ' ');i+=1
    return ''.join(out),state

for f in [root/'src/Plugin.cs',root/'src/QaVerification.cs']:
    src=f.read_text()
    clean,state=strip_csharp(src)
    stack=[]; pairs={'}':'{',')':'(',']':'['}
    bad=None
    for pos,ch in enumerate(clean):
        if ch in '{([': stack.append(ch)
        elif ch in '})]':
            if not stack or stack[-1]!=pairs[ch]:bad=(pos,ch,stack[-1] if stack else None);break
            stack.pop()
    ok(f'{f.name} lexical close',state=='code',f'end state={state}')
    ok(f'{f.name} delimiters',bad is None and not stack,f'bad={bad}, remaining={stack[-10:]}')

plugin=(root/'src/Plugin.cs').read_text(); qav=(root/'src/QaVerification.cs').read_text(); h2=(root/'tools/Run-H2-Gameplay-SelfTest.ps1').read_text(); adapter=(root/'tools/Invoke-QAAdapter.ps1').read_text(); common=(root/'tools/QA-Common.ps1').read_text()
ok('version 0.9.9', 'PluginVersion = "0.9.9"' in plugin and '<Version>0.9.9</Version>' in (root/'UnspottableExpanded.csproj').read_text())
ok('partial verifier hook', 'UpdateQaVerification();' in plugin and 'ExecuteQaVerificationCommand(parts)' in plugin)
ok('legacy protocol retained', any('protocol' in line and ':4' in line for line in plugin.splitlines()))
ok('legacy input commands retained', all(x in plugin for x in ['SETAXIS','PRESS','PROBEAXIS','PROBEBUTTON','CLEARALL']))
ok('no old H2 punch-counter assertion', 'Gameplay polled punch' not in h2 and 'qaInputInterceptCount' not in h2)
ok('H2 no direct scene loads', 'SceneManager.LoadScene' not in plugin)
ok('H2 no manager lifecycle mutation', all(x not in plugin for x in ['InitStartScene', 'resetAllControler', 'AssignKeyboardDebug', 'AssignKeyboardToPlayer', 'EnableJoinGame', 'DisableJoinGame']))
ok('H2 handles Local UI before player selection', 'PostStartMenuScene = "menu_post_start_main"' in plugin and 'TrySubmitQaLocalMenuUi' in plugin and 'LooksLikeQaLocalMenuObject' in plugin and 'InvokeQaUiSubmit' in plugin)
ok('H2 Local UI does not target Online', 'IndexOf("online"' in plugin and 'return false;' in plugin)
ok('H2 requires two normal menu players', 'menuPlayers >= 2' in plugin and 'joining P2 through normal player input' in plugin)
ok('H2 physical START uses movement input', 'DriveQaStartAreaSearch' in plugin and 'MoveX' in plugin and 'MoveY' in plugin)
ok('H2 waits for real gameplay scene', 'IsQaGameplayMainScene' in plugin and 'playerCount < 2' in plugin)
ok('H2 wrapper is rendered', "UE_QA_HEADLESS='0'" in h2 and '-batchmode' not in h2 and '-nographics' not in h2)
ok('H2 rendered run is not low-impact throttled', "UE_QA_LOW_IMPACT='0'" in h2 and "PriorityClass='BelowNormal'" not in h2)
ok('punch execution requires FSM', 'PlayerPunch FSM transitioned' in adapter and 'general getter activity does not count' in adapter)
ok('impact rejects movement-only proof', 'displacement alone' in qav.lower() and 'intentionally insufficient' in adapter)
ok('P2 unsupported is skip', 'P2 is not supported' in adapter and "'SKIP'" in adapter)
ok('cleanup present', 'QA CLEANUP' in adapter and 'INPUT CLEARALL' in adapter and 'CleanupQaVerificationWorld' in qav)
ok('sanitized evidence helper', 'Copy-UEQaSanitizedText' in common and '<REDACTED>' in common)
ok('no raw H2 evidence copy', 'Copy-Item $UnityLog' not in h2 and 'Copy-Item (Join-Path $Game' not in h2)
ok('focused tick avoids scene scan', 'FindObjectsOfType' not in qav)
ok('reaction matching tokenized', 'Regex.Split' in qav and 'Countdown' in qav and 'White' in qav)
ok('cleanup failure reported', 'prepared target could not be restored' in qav and "cleanup.final" in adapter)
ok('player names omitted from lifecycle probe', "PLAYER name='" not in plugin)
ok('hardware model omitted', 'graphicsDeviceName' not in plugin)
ok('safe session omits pid', 'session.safe.json' in (root/'tools/Stop-Background-QA.ps1').read_text() and '@{pid=$session.pid' not in (root/'tools/Stop-Background-QA.ps1').read_text())
# Source docs requested
for name in ['QA_ADAPTER.md','AI_HANDOFF.md','MILESTONES.md','TESTING.md']:
    ok(name+' exists',(root/name).exists())

result={'schemaVersion':'ue.qa.static-validation.v1','status':'PASS' if not errors else 'FAIL','checks':checks,'errors':errors}
(root/'validation/static-validation.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
sys.exit(0 if not errors else 1)
