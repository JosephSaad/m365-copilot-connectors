#!/usr/bin/env python3
# Draws docs/connector-tiers.svg: the five sources, what each connector refuses
# on, the Tier 2 features implemented per connector, the Tier 1 chassis all five
# share, and the path into Microsoft 365.
#
# A GENERATOR RATHER THAN A HAND-DRAWN FILE, for the reason the router's decision
# tree has one: a drawing edited by hand drifts from the code the first time
# somebody adds a connector and does not open a vector editor. Change the data
# below and re-run; the layout is computed.
#
# This is NOT docs/architecture.svg, which answers a different question (how a
# source reaches the index, and by which of the two hosting paths). This one
# answers "what does adding a connector cost, and what does it inherit free".
#
#   render-connector-tiers.py <out.svg>
import sys, pathlib, html
OUT = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "docs/connector-tiers.svg")
NAVY, DEEP, ORANGE = "#0A3B68", "#072C4E", "#EE7623"
GREEN, RED, AMBER = "#4C9A51", "#C0453A", "#C0801C"
INK, MUTED, RULE = "#1B2733", "#55636F", "#D4DCE4"
ALT, WHITE, GROUND = "#E8EEF5", "#FFFFFF", "#F4F7FA"
W, H, M, COLW, GAP = 1660, 1120, 40, 300, 20
COLX = [M + i * (COLW + GAP) for i in range(5)]
SOURCES = [("SQL Server","Tickets · Hierarchy","SqlGraphPush · SqlHierarchyPush"),
           ("Cloudera CDP","Hive · HDFS · Atlas","CdpGraphPush"),
           ("Oracle","Records view","OracleGraphPush"),
           ("Teradata","Records view","TeradataGraphPush"),
           ("MongoDB","Collection · GridFS","MongoGraphPush")]
GUARDS = [(None,["No guard.","RLS and Dynamic Data Masking","are not detected"]),
 (GREEN,["Security zones (CDP-17)","Row filters, masks (CDP-1/2)","Unreadable constructs (CDP-18)","Tag deny or mask (CDP-19)"]),
 (GREEN,["VPD · Label Security","Real Application Security","Data Redaction"]),
 (GREEN,["Row-level constraints","Column-level constraints","Unreadable DBC = stop"]),
 (GREEN,["Views, as a class","Encrypted fields","(CSFLE · Queryable)"])]
TIER2 = ["Per-user guard","Per-item ACL (verifier only)","Incremental read","Sensitivity labels","Binary extraction","Source reconciler"]
MARKS = [["x","x","v","x","-","v"],["~","v","v","~","v","x"],["v","x","v","v","-","v"],["v","x","v","v","-","v"],["v","x","x","v","v","v"]]
FAMILIES = [("PushCore.Sql",0,1),("CdpConnector.Source",1,1),("PushCore.Db",2,2),("Path B — IPushSource",4,1)]
TIER1 = [("PushCore","the engine every connector runs on",["Retry honouring Retry-After","$batch, 20 per request","Concurrent writers","Change detection: content and ACL hashed separately","Delete detection, sweep and guard","(marker, id) checkpoint, frozen on refusal","Duplicate detection","Truncation with markers","Logging redaction","Dry run","Documented exit codes"]),
 ("PushCore.State","durable crawl state",["Item inventory","Run history","Single-instance run lock","Identity cache","Retention"]),
 ("Connector.Security","shared secrets and trust",["Key Vault · Credential Manager","Certificate resolution and rotation","Connection-string refusal","Log scrubbing"])]
p=[]
def esc(t): return html.escape(t, quote=False)
def rect(x,y,w,h,fill=WHITE,stroke=RULE,sw=1.2,rx=4):
    p.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}"/>')
def text(x,y,s,size=13,fill=INK,weight="400",anchor="start",mono=False):
    fam="IBM Plex Mono, ui-monospace, Menlo, monospace" if mono else "IBM Plex Sans, -apple-system, Segoe UI, Roboto, Helvetica, Arial, sans-serif"
    p.append(f'<text x="{x}" y="{y}" font-family="{fam}" font-size="{size}" fill="{fill}" font-weight="{weight}" text-anchor="{anchor}">{esc(s)}</text>')
def band(y,h,label,sub=""):
    rect(M,y,W-2*M,h,GROUND,RULE,1); text(M+14,y+20,label,11,NAVY,"600")
    if sub: text(M+14+len(label)*6.9+14,y+20,sub,11,MUTED)
def arrow(x,y1,y2):
    p.append(f'<path d="M{x},{y1} L{x},{y2}" stroke="{MUTED}" stroke-width="1.6" fill="none" marker-end="url(#a)"/>')
p.append(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="Five connectors and one chassis. SQL Server, Cloudera CDP, Oracle, Teradata and MongoDB are each read by their own connector, which refuses a source it cannot represent faithfully before its first read; SQL Server alone has no such guard. Tier 2 features are implemented per connector and are what adding a source costs. The Tier 1 chassis of PushCore, PushCore.State and Connector.Security is written once and inherited by all five. Everything writes through Microsoft Graph into Microsoft 365 Copilot, Microsoft Search and Copilot Studio agents.">')
p.append(f'<defs><marker id="a" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="{MUTED}"/></marker></defs>')
rect(0,0,W,H,GROUND,GROUND,0,0)
text(M,42,"Five connectors, one chassis",25,NAVY,"600")
p.append(f'<rect x="{M}" y="54" width="56" height="3" fill="{ORANGE}"/>')
text(M,80,"Every source is read by its own connector and written by the same engine. What differs is the reader, and what it refuses to read.",13,MUTED)
y=100
for i,(name,detail,exe) in enumerate(SOURCES):
    x=COLX[i]; rect(x,y,COLW,74,NAVY,DEEP,1.2)
    text(x+14,y+26,name,15,WHITE,"600"); text(x+14,y+46,detail,11.5,"#B9CBDD")
    text(x+14,y+64,exe,9.5,"#8FA9C4",mono=True); arrow(x+COLW/2,y+80,y+100)
y=200
band(y,118,"BEFORE THE FIRST READ","each connector refuses a source it cannot represent faithfully")
for i,(colour,lines) in enumerate(GUARDS):
    x=COLX[i]+8; w=COLW-16
    rect(x,y+30,w,76,"#FBEEEC" if colour is None else WHITE,RULE,1)
    p.append(f'<rect x="{x}" y="{y+30}" width="3.5" height="76" fill="{RED if colour is None else GREEN}"/>')
    for j,ln in enumerate(lines):
        text(x+14,y+50+j*15,ln,10.5,RED if colour is None else INK,"600" if colour is None and j==0 else "400")
    arrow(COLX[i]+COLW/2,y+118,y+138)
y=338
band(y,176,"TIER 2","implemented per connector — this is what adding a source costs")
rowy=y+44
for k,f in enumerate(TIER2): text(M+14,rowy+k*21,f,11,MUTED)
for i in range(5):
    cx=COLX[i]+COLW/2
    for k in range(len(TIER2)):
        g,c={"v":("✓",GREEN),"x":("✗",RED),"~":("~",AMBER),"-":("·","#B4C0CB")}[MARKS[i][k]]
        text(cx,rowy+k*21,g,14,c,"600",anchor="middle")
text(M+14,y+166,"Every connector also supplies a schema, a read, a stable item id and skip accounting — universal, not shown.",10.5,MUTED)
for i in range(5): arrow(COLX[i]+COLW/2,y+176,y+196)
y=534
band(y,76,"SOURCE FAMILY","the shared reader shape, where one exists")
for name,start,span in FAMILIES:
    x=COLX[start]+8; w=COLW*span+GAP*(span-1)-16
    rect(x,y+28,w,36,ALT,NAVY,1.2); text(x+w/2,y+51,name,12.5,NAVY,"600",anchor="middle",mono=True)
arrow(W/2,y+76,y+96)
y=630
band(y,214,"TIER 1","the chassis — written once, inherited by all five, changed by none of them")
bx=M+14; bw=(W-2*M-28-2*14)/3
for n,(title,sub,items) in enumerate(TIER1):
    x=bx+n*(bw+14); rect(x,y+30,bw,170,WHITE,NAVY,1.4)
    p.append(f'<rect x="{x}" y="{y+30}" width="{bw}" height="26" fill="{NAVY}"/>')
    text(x+12,y+48,title,12.5,WHITE,"600",mono=True); text(x+12,y+72,sub,10.5,MUTED)
    for j,it in enumerate(items): text(x+12,y+92+j*15.2,"· "+it,10.5,INK)
arrow(W/2,y+214,y+234)
y=868
rect(M,y,W-2*M,68,NAVY,DEEP,1.2); text(M+20,y+28,"Microsoft Graph",15,WHITE,"600")
text(M+20,y+50,"PUT /external/connections/{id}/items/{itemId}   ·   schema registered once   ·   ACL: ONE AD group per connector   ·   $batch   ·   throttling honoured",11,"#B9CBDD",mono=True)
arrow(W/2,y+68,y+88)
y=956
for i,s in enumerate(["Microsoft 365 Copilot","Microsoft Search","Copilot Studio agents"]):
    w=(W-2*M-2*20)/3; x=M+i*(w+20)
    rect(x,y,w,46,"#EAF2EA",GREEN,1.4); text(x+w/2,y+29,s,13,"#2F6B33","600",anchor="middle")
text(M,H-26,"✓ built    ✗ absent    ~ partial    · not applicable          Tier 2 is what adding a connector costs. Tier 1 is what it inherits free.",11,MUTED,mono=True)
p.append("</svg>")
OUT.write_text("\n".join(p),encoding="utf-8")
print("wrote",OUT,OUT.stat().st_size,"bytes")
