import json
import networkx as nx
from networkx.readwrite import json_graph
from pathlib import Path

data = json.loads(Path('graphify-out/graph.json').read_text(encoding='utf-8'))
G = json_graph.node_link_graph(data, edges='links')

# Find ALL nodes with label containing "AbsolutePath"
ap_nodes = []
for nid, ndata in G.nodes(data=True):
    if 'AbsolutePath' in ndata.get('label', ''):
        ap_nodes.append((nid, ndata, G.degree(nid)))

ap_nodes.sort(key=lambda x: x[2], reverse=True)

print(f"Found {len(ap_nodes)} nodes matching 'AbsolutePath':")
for nid, ndata, deg in ap_nodes[:10]:
    print(f"  {nid} | label={ndata.get('label','')} | degree={deg} | source={ndata.get('source_file','')}")
print()

# Use the highest-degree one
if ap_nodes:
    top_nid = ap_nodes[0][0]
    top_deg = ap_nodes[0][2]
    print(f"Tracing from: {ap_nodes[0][1].get('label','')} (degree={top_deg})")
    print(f"  source: {ap_nodes[0][1].get('source_file','')}")
    print()
    
    neighbors = list(G.neighbors(top_nid))
    print(f"Total neighbors: {len(neighbors)}")
    print()
    
    # Edge type breakdown
    print("Edge type breakdown:")
    edge_types = {}
    for n in neighbors:
        edge_data = G[top_nid][n]
        if isinstance(edge_data, dict):
            rel = edge_data.get('relation', 'unknown')
        else:
            edge_data = list(edge_data.values())[0]
            rel = edge_data.get('relation', 'unknown')
        edge_types[rel] = edge_types.get(rel, 0) + 1
    
    for et, count in sorted(edge_types.items(), key=lambda x: x[1], reverse=True):
        print(f"  {et}: {count}")
    print()
    
    # Show sample neighbors with their source files
    print("Sample connections (first 30):")
    for n in neighbors[:30]:
        nlabel = G.nodes[n].get('label', n)
        nsrc = G.nodes[n].get('source_file', '')
        edge_data = G[top_nid][n]
        if isinstance(edge_data, dict):
            rel = edge_data.get('relation', '')
            conf = edge_data.get('confidence', '')
        else:
            edge_data = list(edge_data.values())[0]
            rel = edge_data.get('relation', '')
            conf = edge_data.get('confidence', '')
        print(f"  --{rel}--> {nlabel} [{conf}] ({nsrc})")