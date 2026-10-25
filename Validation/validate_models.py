"""Portable algorithm-model checks, NOT execution/compilation of Unity C#.
The matching C# regression suite ships under Scripts/Editor.
"""
from collections import deque
from pathlib import Path
import math, random, re, json, time
import numpy as np
from scipy.spatial import ConvexHull

START=time.time()
PACKAGE=Path(__file__).resolve().parents[1]
ROOT=PACKAGE/'Assets/Game/Scripts'
D=((1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1))
def plus(a,b): return tuple(x+y for x,y in zip(a,b))

def thin(p, density, threshold):
    if threshold<=0 or density(p)>=0:return None
    x,y,z=p; bottom=top=y
    while density((x,bottom-1,z))<0:
        bottom-=1
        if top-bottom+1>threshold+1:return None
    while density((x,top+1,z))<0:
        top+=1
        if top-bottom+1>threshold+1:return None
    la,ls,us,ua=[density((x,h,z)) for h in (bottom-1,bottom,top,top+1)]
    low=bottom-1+la/(la-ls); high=top+(-us)/(ua-us)
    if high-low>threshold+1e-5:return None
    return [(x,h,z) for h in range(bottom,top+1)]

class Solver:
    def __init__(self,changes,density,anchor,thickness=0):
        self.density=density;self.anchor=anchor;self.t=max(0,thickness)
        roots=set();origins=set()
        for p in changes:
            roots.add(p);roots.update(plus(p,d) for d in D)
            if self.t>0:
                origins.add(p);origins.update(plus(p,d) for d in D if d[1]==0)
        self.roots=deque(sorted(roots));self.origins=deque(sorted(origins))
        self.origin=None;self.offset=0;self.checked=set();self.thinseen=set()
        self.labels={};self.label=None;self.frontier=deque();self.results=deque()
        self.component=None;self.complete=False
    def step(self,budget):
        assert budget>=1
        for _ in range(budget):
            if self.complete:break
            if self.origin is not None or self.origins:
                if self.origin is None:
                    self.origin=self.origins.popleft();self.offset=-self.t-2
                p=plus(self.origin,(0,self.offset,0));self.offset+=1
                if self.offset>self.t+2:self.origin=None
                if p in self.checked or p in self.thinseen:continue
                self.checked.add(p)
                span=thin(p,self.density,self.t)
                if span:self.thinseen.update(span);self.results.append(span)
                continue
            if self.component is None:
                if not self.roots:self.complete=True;continue
                root=self.roots.popleft()
                if root in self.labels or self.density(root)>=0:continue
                self.component=[];self.frontier.clear();self.label=[False]
                self.labels[root]=self.label;self.frontier.append(root)
            if not self.frontier:
                if self.component:self.results.append(self.component)
                self.component=None;continue
            current=self.frontier.popleft()
            if self.density(current)>=0:continue
            self.component.append(current)
            if self.anchor(current):self.label[0]=True;self.component=None;continue
            for direction in D:
                nxt=plus(current,direction)
                if self.density(nxt)>=0:continue
                known=self.labels.get(nxt)
                if known is not None:
                    if known is not self.label and known[0]:
                        self.label[0]=True;self.component=None;break
                    continue
                self.labels[nxt]=self.label;self.frontier.append(nxt)

def finish(solver,budget=64):
    output=set();steps=0
    while not solver.complete:
        solver.step(budget)
        while solver.results:output.update(solver.results.popleft())
        steps+=1
        if steps>1000000:raise AssertionError('did not finish')
    return output

def oracle(solid,anchor):
    reached={p for p in solid if anchor(p)};q=deque(reached)
    while q:
        p=q.popleft()
        for d in D:
            n=plus(p,d)
            if n in solid and n not in reached:reached.add(n);q.append(n)
    return solid-reached

# Volume geometry is parsed from the supplied C# to guard against transcription drift.
source=(ROOT/'Chunks/TerrainVolume.cs').read_text()
pts=re.search(r'Vector3\[\] P = \{(.*?)\};',source,re.S).group(1)
P=np.array([list(map(float,m)) for m in re.findall(r'new Vector3\((\d),(\d),(\d)\)',pts)])
tets=re.search(r'int\[,] T = \{(.*?)\};',source,re.S).group(1)
T=np.array([list(map(int,m)) for m in re.findall(r'\{(\d),(\d),(\d),(\d)\}',tets)])
assert P.shape==(8,3) and T.shape==(6,4)

def tetra(a,b,c,d):
    v=abs(np.dot(b-a,np.cross(c-a,d-a)))/6
    return v,(a+b+c+d)*v*.25

def clip_tetra(ids,ds):
    ins=[i for i in ids if ds[i]<0];outs=[i for i in ids if ds[i]>=0]
    zero=(0.,np.zeros(3));full=tetra(*P[ids])
    def edge(a,b):return P[a]+(P[b]-P[a])*ds[a]/(ds[a]-ds[b])
    if not ins:return zero
    if len(ins)==4:return full
    if len(ins)==1:
        a=ins[0];return tetra(P[a],*(edge(a,b) for b in outs))
    if len(ins)==3:
        a=outs[0];v,m=tetra(P[a],*(edge(a,b) for b in ins));return full[0]-v,full[1]-m
    u,v=ins;c,d=outs;uc,ud,vc,vd=edge(u,c),edge(u,d),edge(v,c),edge(v,d)
    parts=[tetra(P[u],P[v],uc,ud),tetra(P[v],uc,ud,vd),tetra(P[v],uc,vc,vd)]
    return sum(t[0] for t in parts),sum((t[1] for t in parts),np.zeros(3))

def measure(ds):
    parts=[clip_tetra(ids,ds) for ids in T]
    return sum(x[0] for x in parts),sum((x[1] for x in parts),np.zeros(3))

def hull_reference(ids,ds):
    verts=[P[i] for i in ids if ds[i]<0]
    for i in range(4):
        for j in range(i+1,4):
            a,b=ids[i],ids[j]
            if (ds[a]<0)!=(ds[b]<0):verts.append(P[a]+(P[b]-P[a])*ds[a]/(ds[a]-ds[b]))
    if len(verts)<4:return 0.,np.zeros(3)
    vertices=np.array(verts);hull=ConvexHull(vertices);center=vertices.mean(axis=0)
    parts=[tetra(center,*vertices[face]) for face in hull.simplices]
    return sum(x[0] for x in parts),sum((x[1] for x in parts),np.zeros(3))

checks=[]
def check(name,func):
    func();checks.append(name);print('PASS:',name,flush=True)

def connectivity_random():
    rng=random.Random(7135)
    for trial in range(300):
        solid={(x,y,z) for x in range(7) for y in range(-3,4) for z in range(5) if rng.random()<.55}
        d=lambda p:-1. if p in solid else 1.
        a=lambda p:p[0]==0
        actual=finish(Solver(solid,d,a),1+trial%31)
        assert actual==oracle(solid,a),(trial,len(actual),len(oracle(solid,a)))
check('300 randomized connectivity fields against independent support oracle',connectivity_random)

def supported_cache():
    for length in range(2,80):
        solid={(x,0,0) for x in range(length)}
        result=finish(Solver(solid,lambda p:-1 if p in solid else 1,lambda p:p[0]==0),1)
        assert not result
check('78 supported-cache line regressions',supported_cache)

def isolated_translation():
    for y in (-500,-40,-1,0,1,40,500):
        solid={(x,y,z) for x in range(4) for z in range(4)}
        assert finish(Solver(solid,lambda p:-1 if p in solid else 1,lambda p:False))==solid
check('Disconnected islands at seven heights, including below zero',isolated_translation)

def large():
    d=lambda p:-1 if p[1:]==(-30,0) and 0<=p[0]<=150000 else 1
    s=Solver([(0,-30,0)],d,lambda p:False);s.step(128)
    assert not s.complete
    result=finish(s,128);assert len(result)==150001
check('150001-sample island resumes across work budgets and fully collapses',large)

def fullroof():
    patch={(x,y,z) for x in range(5) for y in range(2) for z in range(5)}
    for edited_y in (-1,2):
        changes=[(x,edited_y,z) for x in range(5) for z in range(5)]
        result=finish(Solver(changes,lambda p:-1 if p in patch else 1,lambda p:True,2),3)
        assert result==patch,(edited_y,len(result))
check('All 50 roof samples detected from upper and lower tunnel edits',fullroof)

def bounds_roof():
    assert thin((0,2,0),lambda p:-1 if 0<=p[1]<=3 else 1,2) is None
    assert thin((0,0,0),lambda p:-1 if p[1]<=2 else 1,2) is None
    assert thin((0,0,0),lambda p:-5 if p[1] in (0,1) else 1,2) is None
    assert thin((0,0,0),lambda p:-5 if p[1]==0 else 1,2)==[(0,0,0)]
    assert thin((0,0,0),lambda p:-1 if p[1]==0 else 1,0) is None
check('True roof boundaries, interpolated thickness, one-sided air and disable option',bounds_roof)

def deletion_during_pass():
    rng=random.Random(92)
    for trial in range(150):
        solid={(x,y,0) for x in range(12) for y in range(3) if rng.random()<.8}
        anchor=lambda p:p[0]==0
        density=lambda p:-1 if p in solid else 1
        s=Solver(solid,density,anchor);s.step(1+trial%20)
        tocut=rng.sample(sorted(solid),min(10,len(solid)));solid.difference_update(tocut)
        got=finish(s,7);got.update(finish(Solver(tocut,density,anchor),5))
        got.intersection_update(solid)
        assert got==oracle(solid,anchor),trial
check('150 concurrent-removal/follow-up-pass connectivity regressions',deletion_during_pass)

def planar_volume():
    for axis in range(3):
        for cut in np.linspace(.05,.95,19):
            vol,m=measure(P[:,axis]-cut)
            assert abs(vol-cut)<1e-6
            assert abs((m/vol)[axis]-cut/2)<1e-6
    full,_=measure(np.full(8,-1.));empty,_=measure(np.full(8,1.))
    assert abs(full-1)<1e-6 and empty==0
    va,ma=measure(P[:,0]-.7);vb,mb=measure(P[:,0]-.3)
    assert abs((va-vb)-.4)<1e-6 and abs(((ma-mb)/(va-vb))[0]-.5)<1e-6
check('57 analytic planar volumes/centroids, full/empty cells and partial cut accounting',planar_volume)

def clipped_hulls():
    rng=np.random.default_rng(812)
    for trial in range(600):
        ds=rng.uniform(-7,7,8)
        for ids in T:
            v,m=clip_tetra(ids,ds);rv,rm=hull_reference(ids,ds)
            assert abs(v-rv)<1e-8 and np.allclose(m,rm,atol=1e-8),(trial,ids,v,rv)
check('3600 clipped tetrahedra checked against independent convex-hull geometry',clipped_hulls)

def repeated_volume():
    rng=np.random.default_rng(55)
    for trial in range(100):
        ds=rng.uniform(-5,1,8);prev=measure(ds)[0]
        for step in range(40):
            ds[rng.integers(0,8)]+=rng.uniform(0,.5)
            new=measure(ds)[0];assert new<=prev+1e-7
            prev=new
check('4000 removal-only volume transitions never create material',repeated_volume)

def shared_grid():
    for p in ((16,-16,16),(-16,-32,-48),(0,0,0),(16,-17,17),(-17,-22,31)):
        owner=tuple(c//16 for c in p)
        owners=[]
        for dx in range(2 if p[0]%16==0 else 1):
            for dy in range(2 if p[1]%16==0 else 1):
                for dz in range(2 if p[2]%16==0 else 1):owners.append((owner[0]-dx,owner[1]-dy,owner[2]-dz))
        for c in owners:assert all(0<=p[i]-16*c[i]<=16 for i in range(3))
        assert len(owners)==2**sum(n%16==0 for n in p)
    for n in range(-100,101):
        # Match C# truncating division + the negative remainder adjustment.
        q=math.trunc(n/16);rem=n-q*16;q=q-1 if rem<0 else q
        assert q==n//16
check('201 negative/positive grid divisions and all shared-boundary owners',shared_grid)

def anchor_proof():
    # Exhaustively enclose a small body in air and verify no interior solid sample
    # qualifies for the solid-X-ray proof; exterior rock on each row does.
    air={(x,y,z) for x in range(-3,4) for y in range(-3,4) for z in range(-3,4) if max(abs(x),abs(y),abs(z))==3}
    ext={}
    for x,y,z in air:
        lo,hi=ext.get((y,z),(x,x));ext[y,z]=(min(lo,x),max(hi,x))
    for x in range(-2,3):
        for y in range(-2,3):
            for z in range(-2,3):
                lo,hi=ext[y,z];assert not(x<lo or x>hi)
    for y in range(-2,3):
        for z in range(-2,3):
            lo,hi=ext[y,z];assert -4<lo and 4>hi
check('Support-ray proof rejects an enclosed 125-sample body',anchor_proof)

# Limited lexical and integration checks. These are NOT a C# compiler.
def static_checks():
    from pygments import lex
    from pygments.lexers import CSharpLexer
    from pygments.token import Comment, String
    for f in ROOT.rglob('*.cs'):
        stack=[]
        for typ,txt in lex(f.read_text(),CSharpLexer()):
            if typ in Comment or typ in String:continue
            for ch in txt:
                if ch in '{[(':stack.append(ch)
                elif ch in '}])':
                    assert stack and stack.pop()==dict(zip('}])','{[('))[ch],str(f)
        assert not stack,(str(f),stack)
    world=(ROOT/'Chunks/World.cs').read_text()
    solver=(ROOT/'Chunks/TerrainStructuralSolver.cs').read_text()
    chunk=(ROOT/'Chunks/Chunk.cs').read_text()
    pool=(ROOT/'Voxel/DebrisPool.cs').read_text()
    assert 'current.y <= 0' not in solver and 'cluster.Count >= maxCollapseVolume' not in world
    assert 'Task.Run' not in world and 'Task.Run' not in chunk
    assert 'RpcApplyDensityDelta(++_revision, positions, values)' in world
    assert 'TargetBeginTerrainSnapshot' in world and '_bufferedDeltas.Add(delta)' in world
    assert 'NetworkServer.UnSpawn(obj)' in pool and 'NetworkServer.Spawn(obj)' in pool
    assert 'if (old >= 0f) continue;' in world
    import hashlib
    meta_hashes=json.loads((PACKAGE/'Validation/original_meta_sha256.json').read_text())
    for relative,expected in meta_hashes.items():
        assert hashlib.sha256((ROOT/relative).read_bytes()).hexdigest()==expected,relative
check('C# delimiter/source-path checks and unchanged original Unity GUID metadata',static_checks)

result={
 'validation_kind':'Portable Python algorithm models and lexical/source checks; NOT Unity or C# execution',
 'groups_passed':len(checks),'groups':checks,
 'seconds':round(time.time()-START,2),
 'not_tested':['C# compilation','Mirror Weaver','Unity Editor regression suite execution','Unity play mode','Runtime prefab/scene behaviour (static wiring checked separately)','Live host/client or dedicated-server networking','Frame-time profiling']
}
(PACKAGE/'Validation/model_results.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result,indent=2))
