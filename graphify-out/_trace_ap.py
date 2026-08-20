import json
import networkx as nx
from networkx.readwrite import json_graph
from pathlib import Path

data = json.loads(Path('graphify-out/graph.json').read_text(encoding='utf-8'))
G = json_graph.node_link_graph(data, edges='links')

# Find the AbsolutePath node
ap_node = None
for nid, ndata in G.nodes(data=True):
    if ndata.get('label') == 'AbsolutePath':
        ap_node = nid
        break

if not ap_node:
    print("AbsolutePath node not found")
    raise SystemExit(1)

print(f"NODE: AbsolutePath")
print(f"  source: {G.nodes[ap_node].get('source_file', 'unknown')}")
print(f"  type: {G.nodes[ap_node].get('file_type', 'unknown')}")
print(f"  degree: {G.degree(ap_node)}")
print()

# Get all neighbors grouped by community
neighbors = list(G.neighbors(ap_node))

# Load community mapping
analysis = json.loads(Path('.graphify_analysis.json').read_text(encoding='utf-8'))
labels = json.loads(Path('.graphify_labels.json').read_text(encoding='utf-8'))

# Build reverse map: node -> community id
node_to_comm = {}
for cid, nodes in analysis['communities'].items():
    for n in nodes:
        node_to_comm[n] = int(cid)

# Group neighbors by community
comm_groups = {}
for n in neighbors:
    cid = node_to_comm.get(n, -1)
    if cid not in comm_groups:
        comm_groups[cid] = []
    comm_groups[cid].append(n)

# Sort by community size (largest first)
sorted_comms = sorted(comm_groups.items(), key=lambda x: len(x[1]), reverse=True)

print(f"Connections to {len(sorted_comms)} communities:")
print()

# Show top 15 communities with edge details
for cid, nodes in sorted_comms[:15]:
    clabel = labels.get(str(cid), f"Cluster {cid}")
    print(f"  [{clabel}] (C{cid}) — {len(nodes)} connections:")
    for n in nodes[:5]:
        nlabel = G.nodes[n].get('label', n)
        nsrc = G.nodes[n].get('source_file', '')
        # Get edge relation
        edge_data = G[ap_node][n]
        if isinstance(edge_data, dict):
            rel = edge_data.get('relation', '')
            conf = edge_data.get('confidence', '')
        else:
            # MultiGraph - get first edge
            edge_data = list(edge_data.values())[0]
            rel = edge_data.get('relation', '')
            conf = edge_data.get('confidence', '')
        print(f"    --{rel}--> {nlabel} [{conf}] ({nsrc})")
    if len(nodes) > 5:
        print(f"    ... +{len(nodes)-5} more")
    print()

# Edge type breakdown
print("Edge type breakdown:")
edge_types = {}
for n in neighbors:
    edge_data = G[ap_node][n]
    if isinstance(edge_data, dict):
        rel = edge_data.get('relation', 'unknown')
    else:
        edge_data = list(edge_data.values())[0]
        rel = edge_data.get('relation', 'unknown')
    edge_types[rel] = edge_types.get(rel, 0) + 1

for et, count in sorted(edge_types.items(), key=lambda x: x[1], reverse=True):
    print(f"  {et}: {count}")