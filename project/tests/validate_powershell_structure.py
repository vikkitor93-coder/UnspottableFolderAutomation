from pathlib import Path
import json, sys
root=Path(__file__).resolve().parents[1]
files=[
 'tools/QA-Common.ps1','tools/Invoke-QAAdapter.ps1','tools/Run-H1-Input-SelfTest.ps1',
 'tools/Run-H2-Gameplay-SelfTest.ps1','tools/Run-Background-QA.ps1',
 'tools/Run-Headless-QA.ps1','tools/Stop-Background-QA.ps1','tools/Show-QA-State.ps1'
]

def mask_ps(src):
    out=[]; i=0; n=len(src); state='code'; line_start=True
    while i<n:
        c=src[i]; d=src[i+1] if i+1<n else ''
        if state=='here_single':
            # Closing marker may be followed by command arguments; it must begin a logical line.
            if line_start and c=="'" and d=='@':
                out.extend('  '); i+=2; state='code'; line_start=False; continue
            out.append('\n' if c=='\n' else ' '); line_start=(c=='\n'); i+=1; continue
        if state=='here_double':
            if line_start and c=='"' and d=='@':
                out.extend('  '); i+=2; state='code'; line_start=False; continue
            out.append('\n' if c=='\n' else ' '); line_start=(c=='\n'); i+=1; continue
        if state=='block_comment':
            if c=='#' and d=='>': out.extend('  '); i+=2; state='code'; line_start=False
            else: out.append('\n' if c=='\n' else ' '); line_start=(c=='\n'); i+=1
            continue
        if state=='single':
            if c=="'" and d=="'": out.extend('  '); i+=2; line_start=False; continue
            if c=="'": state='code'; out.append(' '); i+=1; line_start=False; continue
            out.append('\n' if c=='\n' else ' '); line_start=(c=='\n'); i+=1; continue
        if state=='double':
            if c=='`' and i+1<n: out.extend('  '); i+=2; line_start=False; continue
            if c=='"': state='code'; out.append(' '); i+=1; line_start=False; continue
            out.append('\n' if c=='\n' else ' '); line_start=(c=='\n'); i+=1; continue
        # code
        if c=='<' and d=='#': out.extend('  '); i+=2; state='block_comment'; line_start=False; continue
        if c=='#':
            # line comment
            while i<n and src[i]!='\n': out.append(' '); i+=1
            continue
        if c=='@' and d=="'": out.extend('  '); i+=2; state='here_single'; line_start=False; continue
        if c=='@' and d=='"': out.extend('  '); i+=2; state='here_double'; line_start=False; continue
        if c=="'": state='single'; out.append(' '); i+=1; line_start=False; continue
        if c=='"': state='double'; out.append(' '); i+=1; line_start=False; continue
        out.append(c); line_start=(c=='\n'); i+=1
    return ''.join(out),state

checks=[]; errors=[]
pairs={'}':'{',')':'(',']':'['}
for rel in files:
    p=root/rel; src=p.read_text(errors='replace'); clean,state=mask_ps(src); stack=[]; bad=None
    for pos,ch in enumerate(clean):
        if ch in '{([': stack.append(ch)
        elif ch in '})]':
            if not stack or stack[-1]!=pairs[ch]: bad=(pos,ch,stack[-1] if stack else None); break
            stack.pop()
    passed=state=='code' and bad is None and not stack
    checks.append({'file':rel,'pass':passed,'endState':state,'bad':bad,'remaining':stack[-10:]})
    if not passed: errors.append(rel)
result={'schemaVersion':'ue.qa.powershell-structure.v1','status':'PASS' if not errors else 'FAIL','checks':checks,'errors':errors,'note':'Lexical delimiter/string/comment structure check; not a substitute for the Windows PowerShell parser.'}
(root/'validation/powershell-structure.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
sys.exit(0 if not errors else 1)
