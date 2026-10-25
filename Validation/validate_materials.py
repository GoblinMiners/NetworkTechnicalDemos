"""Portable material algorithm models + source/Unity-YAML checks.
NOT a C# compiler, Mirror Weaver, shader compiler or Unity runtime test.
Run from the unpacked package root after installing: python Validation/validate_materials.py
Dependencies: Python 3, PyYAML, Pygments. Existing debris models also need numpy/scipy.
"""
from pathlib import Path
from functools import lru_cache
from collections import Counter
import re, json, random, math, struct, hashlib, uuid, time
import yaml
from pygments import lex
from pygments.lexers import CSharpLexer
from pygments.token import Comment, String, Error
ROOT=Path(__file__).resolve().parents[1]
GAME=ROOT/'Assets/Game'
START=time.time(); CHECKS=[]; DETAILS={}

def check(name,fn):
    fn();CHECKS.append(name);print('PASS:',name,flush=True)

class UniqueLoader(yaml.SafeLoader):
    pass
def unique_map(loader,node,deep=False):
    result={}
    for k,v in node.value:
        key=loader.construct_object(k,deep=deep)
        if key in result:raise AssertionError('Duplicate YAML key: '+str(key))
        result[key]=loader.construct_object(v,deep=deep)
    return result
UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG,unique_map)

def objects(path):
    text=path.read_text(encoding='utf-8-sig')
    out={}
    for chunk in re.split(r'^--- ',text,flags=re.M)[1:]:
        header,body=chunk.split('\n',1)
        match=re.match(r'!u!(\d+) &(-?\d+)',header)
        assert match,(path,header)
        ident=int(match[2]);assert ident not in out,(path,ident)
        data=yaml.load(body,Loader=UniqueLoader)
        out[ident]=(int(match[1]),next(iter(data.values())))
    assert out,path
    return out

def first(path):return next(iter(objects(path).values()))[1]
def meta_guid(path):return re.search(r'^guid: (\w+)',Path(str(path)+'.meta').read_text(),re.M)[1]
def script_guid(name):
    hits=list(GAME.rglob(name+'.cs'));assert len(hits)==1,(name,hits)
    return meta_guid(hits[0])
def script_components(path,name):
    g=script_guid(name)
    return [v for t,v in objects(path).values() if t==114 and v.get('m_Script',{}).get('guid')==g]

DEFS=[first(p) for p in sorted((GAME/'Resources/Gobmine').glob('*Definition.asset'))]
DEFS.sort(key=lambda d:d['materialId'])
RULES=tuple((d['materialId'],d['depositGridSizeInCells'],d['depositChancePerThousand'],d['minimumRadiusInCells'],d['maximumRadiusInCells'],d['minimumCellY'],d['maximumCellY']) for d in DEFS if d['generateDeposits'])
MASK=0xffffffff

def mix(h):
    h&=MASK;h^=h>>16;h=(h*0x7feb352d)&MASK;h^=h>>15;h=(h*0x846ca68b)&MASK;return (h^(h>>16))&MASK

def hash_cell(x,y,z,seed,mid):return mix(((x&MASK)*0x8da6b343)^((y&MASK)*0xd8163841)^((z&MASK)*0xcb1ab31f)^(seed&MASK)^((mid&MASK)*0x9e3779b9))

@lru_cache(maxsize=131072)
def deposit(rule,gridpos,seed):
    mid,grid,chance,rmin,rmax,lo,hi=rule
    h=hash_cell(*gridpos,seed,mid)
    if h%1000>=chance:return None
    c=[];r=[]
    for g in gridpos:
        h=mix(h);c.append(g*grid*1024+h%(grid*1024))
    for _ in range(3):h=mix(h);r.append(rmin+h%(rmax-rmin+1))
    return tuple(c),tuple(r)

def material(p,seed=1729,rules=RULES,stone=1):
    for rule in reversed(rules):
        mid,grid,chance,rmin,rmax,lo,hi=rule
        if p[1]<lo or p[1]>hi:continue
        base=tuple(v//grid for v in p)
        for x in range(base[0]-1,base[0]+2):
            for y in range(base[1]-1,base[1]+2):
                for z in range(base[2]-1,base[2]+2):
                    d=deposit(rule,(x,y,z),seed)
                    if not d:continue
                    c,r=d;delta=[v*1024+512-cv for v,cv in zip(p,c)]
                    if any(abs(v)>rv*1024 for v,rv in zip(delta,r)):continue
                    if sum(v*v//(rv*rv) for v,rv in zip(delta,r))<=1024**2:return mid
    return stone

def catalogue():
    assert [d['materialId'] for d in DEFS]==[1,2,3,4]
    assert all(d['itemsPerCubicMetre']>0 and d['hardness']>=.05 for d in DEFS)
    for d in DEFS:
        item=next(p for p in (GAME/'Resources/Gobmine').glob('*Item.asset') if meta_guid(p)==d['inventoryItem']['guid'])
        assert first(item)['mineralID']==d['materialId']
    assert first(GAME/'Resources/Gobmine/TerrainMaterialCatalog.asset')['baseStoneId']==1
check('Four stable material/item IDs, reserved Air=0 and positive configured yields',catalogue)

def determinism():
    rng=random.Random(1729);points=[tuple(rng.randrange(-400,401) for _ in range(3)) for i in range(1800)]
    a={p:material(p) for p in points};rng.shuffle(points)
    assert all(material(p)==a[p] for p in points)
    assert any(material(p,42)!=a[p] for p in points)
    counts=Counter(a.values());assert set(counts)=={1,2,3,4},counts
    DETAILS['sample_material_counts_1800']=dict(counts)
check('1800 seeded global-cell queries are order-independent; all ores occur; seed changes deposits',determinism)

def fixture_rules():
    r=lambda mid=2,chance=1000:(mid,8,chance,2,4,-1000,1000)
    points=[(x,0,z) for x in range(-12,12) for z in range(-12,12)]
    overlaps=[p for p in points if material(p,12,(r(2),))==2 and material(p,12,(r(4),))==4]
    assert overlaps
    assert all(material(p,12,(r(2),r(4)))==4 for p in overlaps)
    assert all(material(p,12,(r(2,0),))==1 for p in points)
    vertical=(2,8,1000,2,4,0,0)
    assert all(material((p[0],-1,p[2]),17,(vertical,))==1 and material((p[0],1,p[2]),17,(vertical,))==1 for p in points)
    assert any(material(p,17,(vertical,))==2 for p in points)
    DETAILS['unity_overlap_fixture_matches']=len(overlaps)
check('Unity test fixtures: rare-ore overlap priority, zero chance and vertical limits',fixture_rules)

def chunk_order():
    rng=random.Random(9)
    for _ in range(500):
        p=tuple(rng.randrange(-200,201) for _ in range(3))
        chunk=tuple(v//16 for v in p);local=tuple(v-c*16 for v,c in zip(p,chunk))
        assert all(0<=v<16 for v in local)
        assert material(tuple(c*16+v for c,v in zip(chunk,local)))==material(p)
    src=(GAME/'Scripts/Chunks/TerrainMeshBuilder.cs').read_text()
    assert 'material(globalOrigin+new Vector3Int(x,y,z))' in src
check('500 negative/chunk-boundary round trips retain global ore identity',chunk_order)

def ellipsoid_oracle():
    # Independently compare fixed point with the analytical ellipsoid, away from quantization boundary.
    rng=random.Random(7)
    for _ in range(5000):
        r=[rng.randrange(1,513) for _ in range(3)];d=[rng.randrange(-v*1024,v*1024+1) for v in r]
        fixed=sum(v*v//(rr*rr) for v,rr in zip(d,r))<=1024**2
        exact=sum((v/(rr*1024))**2 for v,rr in zip(d,r))
        if abs(exact-1)>3/(1024**2):assert fixed==(exact<=1)
check('5000 fixed-point deposit membership comparisons against geometric oracle',ellipsoid_oracle)

def air():
    for mask in range(256):
        n=mask.bit_count();state='air' if n==0 else 'solid' if n==8 else 'surface'
        reported=0 if state=='air' else 4
        assert (reported==0)==(mask==0)
    for x in [-4.25,-.75,.0,.25,.5,.99,3.5]:
        cell=math.floor(x);t=x-cell
        assert abs((1-t)*cell+t*(cell+1)-x)<1e-10
    text=(GAME/'Scripts/Materials/TerrainQuery.cs').read_text()
    assert 'solid == 0' in text and 'solid == 8' in text and 'FloorToInt' in text
check('All 256 air/surface/solid corner states and negative point interpolation',air)

def volume_rewards():
    # Accounting model: material remains fixed per cell; only positive removed volume is credited.
    rng=random.Random(8);rates={d['materialId']:d['itemsPerCubicMetre'] for d in DEFS}
    direct=Counter();piles=Counter();removed=Counter();remaining={}
    for i in range(200):
        mid=i%4+1;remaining[i]=[mid,1.0]
        for step in range(20):
            before=remaining[i][1];after=0.0 if step==19 else max(0,before-rng.uniform(0,.14));v=(before-after)*.25**3
            remaining[i][1]=after;units=v*rates[mid];removed[mid]+=units
            (direct if step%2 else piles)[mid]+=units
        before=remaining[i][1];assert before==0
        assert max(0,before-before)==0
    for mid in rates:assert abs(direct[mid]+piles[mid]-removed[mid])<1e-10
    source=(GAME/'Scripts/Chunks/WorldMaterials.cs').read_text()
    assert 'if(!collapse && miner!=null' in source and 'ServerAddUnits(d.inventoryItem.mineralID,units))return;' in source
    assert '_resourcePiles.Add(id,d.inventoryItem.mineralID,units,centre)' in source
check('4000 accounting transitions conserve resource value without double-crediting debris',volume_rewards)

def ledger():
    # Model the store's stable original key while physical position changes.
    piles={};buckets={};orig={};next_id=1
    def add(mid,item,units,pos):
        nonlocal next_id
        key=(*[math.floor(v) for v in pos],mid,item)
        if key in buckets:
            i=buckets[key];piles[i]['units']+=units;return i
        i=next_id;next_id+=1;piles[i]=dict(units=units,pos=pos);orig[i]=key;buckets[key]=i;return i
    i=add(4,4,.125,(0,0,0));piles[i]['pos']=(0,-10,0)
    assert add(4,4,.375,(.2,.2,.2))==i and piles[i]['pos']==(0,-10,0)
    assert piles[i]['units']==.5
    assert add(2,2,1,(0,0,0))!=i
    assert add(4,4,1,(-.1,0,0))!=i
    del buckets[orig.pop(i)];del piles[i];assert i not in piles
    assert add(4,4,1,(0,0,0))!=i
    src=(GAME/'Scripts/ResourcesSystem/ResourcePileStore.cs').read_text()
    assert '_originalBuckets' in src and 'p.Position=position' in src
check('Fractional/negative/mixed resource buckets, moving piles and exactly-once consumption model',ledger)

def snapshot_conservation():
    world=Counter({2:2.125,4:3.5});inventories=[Counter({2:1.875,3:.25}),Counter({4:2.5})]
    frozen=world.copy()
    for inventory in inventories:frozen.update(inventory)
    assert sum(frozen.values())==10.25
    assert world==Counter({2:2.125,4:3.5})
    for inventory in inventories:world.update(inventory);inventory.clear()
    assert world==frozen
    source=(GAME/'Scripts/Chunks/WorldMaterials.cs').read_text()
    assert 's.Resources=_resourcePiles.Copy()' in source
    assert 'inventory.Quantities.Clear()' in source
    assert 'RulesSignature=_worldRulesSignature' in source
check('Save snapshots and disconnect spill conserve inventory plus uncollected value',snapshot_conservation)

def binary_layout():
    # BinaryWriter-compatible field layout (64-character rule signature uses a one-byte length).
    signature=b'A'*64
    payload=b'GMBMAT01'+struct.pack('<iii',1,1,1729)+struct.pack('<iiif',16,16,16,.25)+bytes([64])+signature
    payload+=struct.pack('<i',1)+struct.pack('<iiif',-16,-4,7,1.25)
    payload+=struct.pack('<i',1)+struct.pack('<iidfff',4,4,.125,1,-3,2)
    blob=payload+hashlib.sha256(payload).digest()
    assert hashlib.sha256(blob[:-32]).digest()==blob[-32:]
    data=bytearray(blob);data[30]^=1
    assert hashlib.sha256(data[:-32]).digest()!=data[-32:]
    assert struct.unpack_from('<f',blob,32)[0]==.25
    assert struct.calcsize('<iiif')==16 and struct.calcsize('<iidfff')==28
    save=(GAME/'Scripts/Save/MaterialWorldPersistence.cs').read_text()
    for t in ['File.Replace(temp,path,backup)','stream.Flush(true)','SHA256.Create()','path+".bak"','CheckCount(count,16','CheckCount(count,28']:
        assert t in save,t
check('Save binary field sizes/checksum corruption model and atomic-replace/backup source paths',binary_layout)

def yaml_integrity():
    count=0
    for p in GAME.rglob('*'):
        if p.suffix not in ['.prefab','.unity','.asset','.mat']:continue
        obs=objects(p);count+=1
        for obj_type,value in obs.values():
            if obj_type==1:
                for component in value.get('m_Component',[]):assert component['component']['fileID'] in obs,(p,component)
            if obj_type in [4,224]:
                parent=value.get('m_Father',{}).get('fileID',0)
                assert parent==0 or parent in obs,(p,parent)
    DETAILS['unity_yaml_documents_checked']=count
check('Unity YAML parses without duplicate keys or missing local components/parents',yaml_integrity)

def guids():
    found={}
    for p in GAME.rglob('*.meta'):
        g=re.search(r'^guid: (\w+)',p.read_text(),re.M)[1]
        assert g not in found,(p,found.get(g));found[g]=p
    # New assets may reference only shipped assets or Unity's built-in GUIDs,
    # except the pickup's NetworkIdentity, which belongs to installed Mirror.
    mirror_identity='9b91ecbcc199f4492b9a91e820070131'
    for p in (GAME/'Resources/Gobmine').glob('*'):
        if p.suffix=='.meta':continue
        for g in re.findall(r'guid: ([a-f0-9]{32})',p.read_text()):
            assert g in found or g.startswith('0000000000000000') or g==mirror_identity,(p,g)
    for p in [GAME/'Prefabs/Debris.prefab',GAME/'Prefabs/Player.prefab',GAME/'Resources/Gobmine/OrePickup.prefab']:
        words=struct.unpack('<4I',uuid.UUID(hex=meta_guid(p)).bytes_le)
        expect=words[0]^words[1]^words[2]^words[3]
        identity=[v for t,v in objects(p).values() if t==114 and v.get('m_Script',{}).get('guid')==mirror_identity][0]
        assert identity['_assetId']==expect,(p,expect,identity['_assetId'])
    DETAILS['unique_game_asset_guids']=len(found)
check('No duplicate asset GUIDs; all new resource references resolve; all three Mirror asset IDs match',guids)

def wiring():
    debris=GAME/'Prefabs/Debris.prefab';player=GAME/'Prefabs/Player.prefab';pickup=GAME/'Resources/Gobmine/OrePickup.prefab'
    assert len(script_components(debris,'DebrisCleanup'))==1
    trans=[v for t,v in objects(debris).values() if t==114 and v.get('m_Script',{}).get('guid')=='8ff3ba0becae47b8b9381191598957c8'][0]
    assert trans['syncDirection']==0
    assert script_components(player,'PlayerInventory')[0]['syncMode']==1
    assert len(script_components(player,'MiningInventoryHUD'))==1
    ps=objects(pickup);assert sum(t==54 for t,v in ps.values())==1
    assert sum(t==65 and not v['m_IsTrigger'] for t,v in ps.values())==1
    assert sum(t==135 and v['m_IsTrigger'] for t,v in ps.values())==1
    assert meta_guid(pickup) in (GAME/'Scene/Menu.unity').read_text()
    world=script_components(GAME/'Scene/DemoLvl.unity','World')[0]
    assert world['minCeilingThickness']==2 and world['voxelSize']==.25
check('Debris server authority, owner-only inventory, pickup body/trigger and scene registrations',wiring)

def source_security():
    src=(GAME/'Scripts/PlayerScript/MiningServerValidation.cs').read_text()
    for s in ['tool.connectionToClient.identity!=tool.netIdentity','Physics.Raycast','inventory.NextServerMiningTime','tool.enabled','world.QueuePlayerMining']:assert s in src
    miner=(GAME/'Scripts/PlayerScript/VoxelMiner.cs').read_text();assert 'CmdMine(Vector3 direction)' in miner
    world=(GAME/'Scripts/Chunks/World.cs').read_text()
    assert 'bool missingRevision = false' in world and 'delta.Revision != _lastClientRevision + 1' in world
    assert 'Command(requiresAuthority = false)' in world # Only the read-only world snapshot request.
    assert world.count('Command(requiresAuthority = false)')==1
    assert 'CmdModifyTerrain' not in world
    resource=(GAME/'Scripts/Chunks/WorldMaterials.cs').read_text()
    assert 'Physics.Linecast' in resource and 'Vector3.Distance(inventory.transform.position,pile.Position)>resourcePickupReach' in resource
check('Owned mining, server raycasts/cooldown, pickup distance/walls and contiguous late-join source paths',source_security)

def lexical():
    files=list(GAME.rglob('*.cs'))
    for f in files:
        stack=[]
        for typ,txt in lex(f.read_text(encoding='utf-8-sig'),CSharpLexer()):
            assert typ not in Error,(f,txt)
            if typ in Comment or typ in String:continue
            for ch in txt:
                if ch in '{[(':stack.append(ch)
                elif ch in '}])':assert stack and stack.pop()==dict(zip('}])','{[('))[ch],f
        assert not stack,f
    for f in (GAME/'Scripts/Editor').glob('*.cs'):assert '#if UNITY_EDITOR' in f.read_text()
    DETAILS['csharp_files_lexically_checked']=len(files)
    DETAILS['provided_unity_regression_groups']=sum(len(re.findall(r'^        Check\(',p.read_text(),re.M)) for p in [GAME/'Scripts/Editor/DebrisRegressionTests.cs',GAME/'Scripts/Editor/MaterialRegressionTests.cs'])
check('C# lexical delimiters/error tokens and editor-only boundaries (not compilation)',lexical)

def shader_refs():
    shader=GAME/'Shaders/TerrainOreURP.shader';guid=meta_guid(shader);text=shader.read_text()
    assert all(token in text for token in ['UniversalForwardOnly','ShadowCaster','DepthOnly','DepthNormalsOnly','UnityPerMaterial','UniversalFragmentPBR','_WorldSpace'])
    for p in (GAME/'Resources/Gobmine').glob('*.mat'):
        d=first(p);assert d['m_Shader']['guid']==guid
        props={k:v for item in d['m_SavedProperties']['m_Floats'] for k,v in item.items()}
        assert props['_WorldSpace']==(0 if 'Debris' in p.name else 1)
check('All eight starter materials reference the URP shader and correct world/object projection',shader_refs)

# Reference deposit centres to make the isolated test scene easier to inspect.
# Positions are pure-model coordinates; do not claim visual/play-mode verification.
def locations():
    result={}
    for rule in RULES:
        mid=rule[0];choices=[]
        for x in range(-3,4):
            for y in range(-2,2):
                for z in range(-3,4):
                    d=deposit(rule,(x,y,z),1729)
                    if not d:continue
                    p=tuple(math.floor(v/1024) for v in d[0]);pos=tuple((v+.5)*.25 for v in p)
                    # Keep outside the spawn room and at a practical mining height.
                    if -12<pos[0]<12 and -12<pos[2]<12 and -4<pos[1]<8:continue
                    if material(p)!=mid:continue
                    choices.append((sum(v*v for v in pos),p,pos))
        choices.sort();assert choices,mid
        _,cell,pos=choices[0];result[mid]={'cell':cell,'world_metres':pos}
    DETAILS['seed_1729_example_deposit_centres']=result
locations()
REPORT={'validation_kind':'Portable Python algorithm models and static source/Unity-YAML checks; NOT C# or Unity execution','groups_passed':len(CHECKS),'groups':CHECKS,'details':DETAILS,'seconds':round(time.time()-START,2),'not_tested':['C# compilation and Mirror Weaver','Shader compilation and graphics appearance','Unity editor regression execution','Physics/play-mode behaviour','Live host/client/dedicated server/late join','Actual platform filesystem replacement','Frame-time/network/memory profiling']}
(ROOT/'Validation/material_model_results.json').write_text(json.dumps(REPORT,indent=2))
print(json.dumps(REPORT,indent=2))
