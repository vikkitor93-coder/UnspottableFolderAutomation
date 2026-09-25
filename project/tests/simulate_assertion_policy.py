from pathlib import Path
import json, sys
root=Path(__file__).resolve().parents[1]
cases=[]

def classify(*, injected, consumed, moved, fsm_transition, execution_delay_s, target_available, reaction, reaction_delay_s, target_displacement, p2_supported):
    a=[]
    execution_ok=bool(consumed and fsm_transition and execution_delay_s <= 2.0)
    reaction_ok=bool(execution_ok and reaction and reaction_delay_s <= 2.0)
    a.append(('input_injection','PASS' if injected else 'FAIL'))
    a.append(('input_consumption','PASS' if consumed else 'FAIL'))
    a.append(('world_movement','PASS' if moved else 'FAIL'))
    a.append(('punch_execution','PASS' if execution_ok else 'FAIL'))
    if not target_available:
        a.append(('punch_impact','SKIP'))
    else:
        a.append(('punch_impact','PASS' if reaction_ok else 'FAIL'))
    a.append(('p2_independent','PASS' if p2_supported else 'SKIP'))
    critical=[s for n,s in a if n!='p2_independent']
    overall='FAIL' if 'FAIL' in critical else ('SKIP' if 'SKIP' in critical else 'PASS')
    return overall,dict(a)

def case(name, expected, **kw):
    overall,a=classify(**kw); passed=overall==expected
    cases.append({'name':name,'pass':passed,'expected':expected,'actual':overall,'assertions':a,'inputs':kw})

base=dict(injected=True,consumed=True,moved=True,target_available=True,target_displacement=0.0,p2_supported=False,execution_delay_s=0.15,reaction_delay_s=0.10)
case('getter-only punch is not execution','FAIL',**base,fsm_transition=False,reaction=False)
case('movement-only target change is not impact','FAIL',**{**base,'target_displacement':0.8},fsm_transition=True,reaction=False)
case('fsm execution plus reaction can pass with P2 unsupported','PASS',**base,fsm_transition=True,reaction=True)
case('missing deterministic target is skip not pass','SKIP',**{**base,'target_available':False},fsm_transition=True,reaction=False)
case('late unrelated PlayerPunch transition is rejected','FAIL',**{**base,'execution_delay_s':2.5},fsm_transition=True,reaction=True)
case('late target reaction is rejected','FAIL',**{**base,'reaction_delay_s':2.5},fsm_transition=True,reaction=True)
result={'schemaVersion':'ue.qa.policy-simulation.v1','status':'PASS' if all(c['pass'] for c in cases) else 'FAIL','note':'Simulation of assertion policy only; does not execute Unity or game code. Correlation windows are 2.0 s.','cases':cases}
(root/'validation/policy-simulation.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
sys.exit(0 if result['status']=='PASS' else 1)
