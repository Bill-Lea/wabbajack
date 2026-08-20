import json, networkx as nx
from networkx.readwrite import json_graph
from pathlib import Path
from collections import Counter

data = json.loads(Path('graphify-out/graph.json').read_text(encoding='utf-8'))
G = json_graph.node_link_graph(data, edges='links')

zero_deg = [n for n in G.nodes() if G.degree(n) == 0]
one_deg = [n for n in G.nodes() if G.degree(n) == 1]

lines = []
lines.append(f"Degree 0: {len(zero_deg)}")
lines.append(f"Degree 1: {len(one_deg)}")
lines.append(f"Total deg<=1: {len(zero_deg) + len(one_deg)}")
lines.append("")

# Sample degree-0
lines.append("=== Degree-0 samples ===")
for n in zero_deg[:15]:
    nd = G.nodes[n]
    lines.append(f"  {nd.get('label', n)} | src={nd.get('source_file', '')} | type={nd.get('file_type', '')}")
lines.append("")

# Sample degree-1
lines.append("=== Degree-1 samples ===")
for n in one_deg[:25]:
    nd = G.nodes[n]
    neighbors = list(G.neighbors(n))
    ninfo = []
    for nb in neighbors[:2]:
        nl = G.nodes[nb].get('label', nb)
        ninfo.append(f"-> {nl}")
    lines.append(f"  {nd.get('label', n)} | src={nd.get('source_file', '')} | {ninfo}")
lines.append("")

# Type breakdown
lines.append("=== Type breakdown (deg<=1) ===")
tc = Counter()
for n in zero_deg + one_deg:
    tc[G.nodes[n].get('file_type', 'unknown')] += 1
for t, c in tc.most_common():
    lines.append(f"  {t}: {c}")
lines.append("")

# Label categories
lines.append("=== Label categories (deg<=1) ===")
prims = {'char','byte','string','int','bool','uint','ulong','short','float','double','object','void','var','Uri','Version','Task','CancellationToken','DateTime','Action','Func','Dictionary','List','HashSet','IEnumerable','Stream','MemoryStream','SemaphoreSlim','ConcurrentDictionary','IComparable','IEquatable','IReadOnlySet','ReadOnlyObservableCollection','ReactiveCommand','Unit','HttpClient','Lazy','ICollection','ConcurrentBag','Fact','ICommand','TwoFactorKey','ResourceDictionary'}
cats = Counter()
for n in zero_deg + one_deg:
    label = G.nodes[n].get('label', n)
    if label in prims:
        cats['BCL primitive/type'] += 1
    elif label.startswith('csharp_namespace:'):
        cats['hashed namespace'] += 1
    elif label.startswith('.') or '(' in label:
        cats['method/anonymous'] += 1
    elif label and label[0].islower():
        cats['variable/parameter'] += 1
    else:
        cats['class/type name'] += 1
for c_name, c_count in cats.most_common():
    lines.append(f"  {c_name}: {c_count}")
lines.append("")

# Source projects for deg-0
lines.append("=== Top source projects (deg-0) ===")
src_counts = Counter()
for n in zero_deg:
    src = G.nodes[n].get('source_file', '')
    if src:
        parts = src.replace('\\', '/').split('/')
        src_counts[parts[0]] += 1
    else:
        src_counts['(no source)'] += 1
for s, c in src_counts.most_common(20):
    lines.append(f"  {s}: {c}")

output = '\n'.join(lines)
Path('graphify-out/_weak_result.txt').write_text(output, encoding='utf-8')
print(output)