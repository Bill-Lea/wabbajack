import json
import networkx as nx
from networkx.readwrite import json_graph
from pathlib import Path
from collections import Counter

data = json.loads(Path('graphify-out/graph.json').read_text(encoding='utf-8'))
G = json_graph.node_link_graph(data, edges='links')

# Find weakly connected components
components = list(nx.weakly_connected_components(G))
components.sort(key=len, reverse=True)

print(f"Total components: {len(components)}")
print(f"Largest component: {len(components[0])} nodes")
print()

# The question mentioned 1222 weakly connected nodes - let's look at nodes NOT in the main component
# or nodes that are in small isolated components
small_comps = [c for c in components if len(c) < 5]
isolated_nodes = [c for c in components if len(c) == 1]
tiny_comps = [c for c in components if 2 <= len(c) <= 4]

print(f"Isolated nodes (degree 0): {len(isolated_nodes)}")
print(f"Tiny components (2-4 nodes): {len(tiny_comps)} ({sum(len(c) for c in tiny_comps)} nodes)")
print(f"Small components (<5 nodes): {len(small_comps)} ({sum(len(c) for c in small_comps)} nodes)")
print()

# Check for nodes with very low degree (<=1) in the main graph
low_degree = [(n, G.degree(n)) for n in G.nodes() if G.degree(n) <= 1]
print(f"Nodes with degree <= 1: {len(low_degree)}")
print()

# Sample the isolated/low-degree nodes and see what they are
print("=== Sample isolated nodes (degree 0) ===")
for n in list(isolated_nodes)[:20]:
    ndata = G.nodes[n]
    print(f"  {ndata.get('label', n)} | source={ndata.get('source_file', '')} | type={ndata.get('file_type', '')}")

print()
print("=== Sample low-degree nodes (degree 1) ===")
low_deg_sample = [(n, d) for n, d in low_degree if d == 1][:30]
for n, d in low_deg_sample:
    ndata = G.nodes[n]
    # Show what they connect to
    neighbors = list(G.neighbors(n))
    neigh_labels = [G.nodes[nb].get('label', nb) for nb in neighbors[:3]]
    print(f"  {ndata.get('label', n)} (deg={d}) | source={ndata.get('source_file', '')} | connects to: {neigh_labels}")

print()
print("=== Node type breakdown for low-degree nodes ===")
type_counts = Counter()
for n, d in low_degree:
    ft = G.nodes[n].get('file_type', 'unknown')
    type_counts[ft] += 1
for t, c in type_counts.most_common():
    print(f"  {t}: {c}")

print()
# Look at what kinds of labels these low-degree nodes have
print("=== Label patterns in low-degree nodes ===")
label_patterns = Counter()
for n, d in low_degree:
    label = G.nodes[n].get('label', n)
    # Categorize the label
    if label in ('char', 'byte', 'string', 'int', 'bool', 'uint', 'ulong', 'short', 'float', 'double', 'object', 'void', 'var', 'Uri', 'Version', 'Task', 'CancellationToken', 'DateTime', 'Action', 'Func', 'Dictionary', 'List', 'HashSet', 'IEnumerable', 'Stream', 'MemoryStream', 'SemaphoreSlim', 'ConcurrentDictionary'):
        label_patterns['BCL primitive/type'] += 1
    elif label.startswith('csharp_namespace:'):
        label_patterns['hashed namespace'] += 1
    elif '((' in label or '))' in label:
        label_patterns['anonymous/method'] += 1
    elif '.' in label and '(' in label:
        label_patterns['method call'] += 1
    elif label[0].islower() if label else False:
        label_patterns['variable/parameter'] += 1
    else:
        label_patterns['other'] += 1
for p, c in label_patterns.most_common():
    print(f"  {p}: {c}")