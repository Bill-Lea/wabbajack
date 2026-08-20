import json
import networkx as nx
from networkx.readwrite import json_graph
from pathlib import Path
from collections import Counter

data = json.loads(Path('graphify-out/graph.json').read_text(encoding='utf-8'))
G = json_graph.node_link_graph(data, edges='links')

top_nid = 'wabbajack_paths_absolutepath_wabbajack_paths_absolutepath'
neighbors = list(G.neighbors(top_nid))

# Group by source project (first folder in source_file path)
project_counts = Counter()
project_examples = {}
for n in neighbors:
    src = G.nodes[n].get('source_file', '')
    if src:
        parts = src.replace('\\', '/').split('/')
        project = parts[0] if parts else 'unknown'
        project_counts[project] += 1
        if project not in project_examples:
            project_examples[project] = []
        if len(project_examples[project]) < 3:
            nlabel = G.nodes[n].get('label', n)
            project_examples[project].append(nlabel)

print("AbsolutePath connections by project:")
print()
for proj, count in project_counts.most_common():
    examples = project_examples.get(proj, [])
    ex_str = ', '.join(examples)
    print(f"  {proj}: {count} connections")
    print(f"    e.g: {ex_str}")
    print()

# Also show what types of things reference it
print("\nNode types referencing AbsolutePath:")
type_counts = Counter()
for n in neighbors:
    ft = G.nodes[n].get('file_type', 'unknown')
    type_counts[ft] += 1
for t, c in type_counts.most_common():
    print(f"  {t}: {c}")