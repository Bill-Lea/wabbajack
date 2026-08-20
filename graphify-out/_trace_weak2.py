import json
import networkx as nx
from networkx.readwrite import json_graph
from pathlib import Path
from collections import Counter

data = json.loads(Path('graphify-out/graph.json').read_text(encoding='utf-8'))
G = json_graph.node_link_graph(data, edges='links')

# Use undirected for connectivity
Gu = G.to_undirected()

# Find connected components
components = list(nx.connected_components(Gu))
components.sort(key=len, reverse=True)

print(f"Total connected components: {len(components)}")
print(f"Largest: {len(components[0])}")
print(f"Nodes outside largest: {sum(len(c) for c in components[1:])}")
print()

# Count isolated and small components
isolated = [c for c in components if len(c) == 1]
tiny = [c for c in components if 2 <= len(c) <= 4]
small = [c for c in components if len(c) < 10]
print(f"Isolated (1 node): {len(isolated)}")
print(f"Tiny (2-4 nodes): {len(tiny)} ({sum(len(c) for c in tiny)} nodes)")
print(f"Small (<10 nodes): {len(small)} ({sum(len(c) for c in small)} nodes)")
print()

# The report said 1222 weakly connected - look at nodes with degree 0 or 1
zero_deg = [n for n in G.nodes() if G.degree(n) == 0]
one_deg = [(n, G.degree(n)) for n in G.nodes() if G.degree(n) == 1]
print(f"Degree 0 (no edges): {len(zero_deg)}")
print(f"Degree 1 (single edge): {len(one_deg)}")
print(f"Total deg<=1: {len(zero_deg) + len(one_deg)}")
print()

# Sample degree-0 nodes
if zero_deg:
    print("=== Sample degree-0 nodes ===")
    for n in zero_deg[:15]:
        ndata = G.nodes[n]
        print(f"  {ndata.get('label', n)} | src={ndata.get('source_file', '')} | type={ndata.get('file_type', '')}")
    print()

# Sample degree-1 nodes
if one_deg:
    print("=== Sample degree-1 nodes (first 25) ===")
    for n, d in one_deg[:25]:
        ndata = G.nodes[n]
        neighbors = list(G.neighbors(n))
        neigh_info = []
        for nb in neighbors[:2]:
            nl = G.nodes[nb].get('label', nb)
            ed = G[n][nb]
            if not isinstance(ed, dict):
                ed = list(ed.values())[0]
            rel = ed.get('relation', '')
            neigh_info.append(f"--{rel}--> {nl}")
        print(f"  {ndata.get('label', n)} | src={ndata.get('source_file', '')} | {neigh_info}")
    print()

# Type breakdown for deg<=1
print("=== Type breakdown (deg<=1) ===")
tc = Counter()
for n in zero_deg:
    tc[G.nodes[n].get('file_type', 'unknown')] += 1
for n, d in one_deg:
    tc[G.nodes[n].get('file_type', 'unknown')] += 1
for t, c in tc.most_common():
    print(f"  {t}: {c}")
print()

# Label categorization
print("=== Label categories (deg<=1) ===")
cats = Counter()
for n in zero_deg + [n for n, d in one_deg]:
    label = G.nodes[n].get('label', n)
    primitives = {'char','byte','string','int','bool','uint','ulong','short','float','double','object','void','var','Uri','Version','Task','CancellationToken','DateTime','Action','Func','Dictionary','List','HashSet','IEnumerable','Stream','MemoryStream','SemaphoreSlim','ConcurrentDictionary','IComparable','IEquatable','IReadOnlySet','ReadOnlyObservableCollection','ReactiveCommand','Unit','HttpClient','Lazy','ICollection','ConcurrentBag','Fact','TextCompositionEventArgs','ICommand','TwoFactorKey','ResourceDictionary','int','bool'}
    if label in primitives:
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
    print(f"  {c_name}: {c_count}")
print()

# Source file breakdown for deg-0
print("=== Top source files for degree-0 nodes ===")
src_counts = Counter()
for n in zero_deg:
    src = G.nodes[n].get('source_file', '')
    if src:
        parts = src.replace('\\', '/').split('/')
        src_counts[parts[0]] += 1
    else:
        src_counts['(no source)'] += 1
for s, c in src_counts.most_common(15):
    print(f"  {s}: {c}")