﻿using GraphVisualizer.Data;
using GraphVisualizer.Utils;
using Microsoft.Extensions.Caching.Memory;
using VDS.RDF;
using VDS.RDF.Query;

namespace GraphVisualizer.DataAccess;

public class SparqlRepository : ISparqlRepository
{
    private const string _graphCacheKey = "graphCacheKey";
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _memoryCache;
    private readonly HttpClient _httpClient;

    public SparqlRepository(IConfiguration configuration, IMemoryCache memoryCache, HttpClient httpClient)
    {
        _configuration = configuration;
        _memoryCache = memoryCache;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    public Task<SparqlResultSet> Search(string searchTerm)
    {
        try
        {
            Uri endpointUri = new(_configuration.GetValue<string>("Sparql:BaseUrl") ?? string.Empty);
            SparqlQueryClient sparqlQueryClient = new(_httpClient, endpointUri);

            string query = $@"
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT DISTINCT ?resource ?label WHERE {{
  ?resource rdfs:label ?label .
  FILTER(regex(?label, ""{searchTerm}"", ""i"") && langMatches(lang(?label), ""EN""))
}}
LIMIT 100";

            return sparqlQueryClient.QueryWithResultSetAsync(query);
        }
        catch
        {
            return Task.FromResult(new SparqlResultSet());
        }
    }

    public async Task<KnowledgeGraph> Get(string uri, int loadingDepth, int limit)
    {
        if (!_memoryCache.TryGetValue(_graphCacheKey, out Dictionary<string, Graph>? graphDictionary))
        {
            graphDictionary = [];
            _memoryCache.Set(_graphCacheKey, graphDictionary);
        }

        if (!graphDictionary!.TryGetValue(uri, out Graph? graph))
        {
            try
            {
                graph = (Graph?)await LoadGraphAsync(uri);
            }
            catch (Exception ex)
            {
                // Log the exception (using your preferred logging approach)
                Console.WriteLine($"Failed to load graph for URI: {uri}. Exception: {ex.Message}");
                graph = null;
            }

            if (graph == null)
            {
                return new KnowledgeGraph()
                {
                    Nodes = [new Node() { Uri = uri }]
                };
            }

            graphDictionary[uri] = graph;
        }

        KnowledgeGraph knowledgeGraph = GraphHelper.ConvertGraphToKnowledgeGraph(graph, limit);
        System.Console.WriteLine("initial load finished: " + knowledgeGraph.Nodes.Count);

        if (loadingDepth > 1)
        {
            await LoadSubGraphsAsync(knowledgeGraph, loadingDepth - 1, limit, graphDictionary);
        }

        System.Console.WriteLine("Graph load finished: " + knowledgeGraph.Nodes.Count);
        return knowledgeGraph;
    }

    private async Task LoadSubGraphsAsync(KnowledgeGraph knowledgeGraph, int remainingDepth, int limit, Dictionary<string, Graph> graphDictionary)
    {
        var tasks = new List<Task>();

        foreach (var node in knowledgeGraph.Nodes)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var subGraph = await Get(node.Uri, remainingDepth, limit);
                    System.Console.WriteLine("subgraph finished: " + subGraph.Nodes.Count);
                    lock (knowledgeGraph.Nodes)
                    {
                        knowledgeGraph.Nodes.AddRange(subGraph.Nodes);
                    }
                }
                catch (Exception ex)
                {
                    // Log the exception (using your preferred logging approach)
                    Console.WriteLine($"Failed to load subgraph for URI: {node.Uri}. Exception: {ex.Message}");
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    public Task<IGraph> LoadGraphAsync(string uri)
    {
        try
        {
            Uri endpointUri = new(_configuration.GetValue<string>("Sparql:BaseUrl") ?? string.Empty);
            SparqlQueryClient sparqlQueryClient = new(_httpClient, endpointUri);

            string query = $@"
                CONSTRUCT {{
                    ?s ?p ?o .
                }}
                WHERE {{
                    ?s ?p ?o .
                    FILTER(?s = <{uri}>)
                }}";

            // Create a CancellationTokenSource
            CancellationTokenSource tokenSource = new(2000);
            return sparqlQueryClient.QueryWithResultGraphAsync(query, tokenSource.Token);
        }
        catch
        {
            return Task.FromResult((IGraph)new Graph());
        }
    }
}
