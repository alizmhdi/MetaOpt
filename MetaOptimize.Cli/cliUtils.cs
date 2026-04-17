namespace MetaOptimize.Cli
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Newtonsoft.Json.Linq;
    /// <summary>
    /// Implements a utility function with some .
    /// </summary>
    /// TODO: incomplete comment
    public static class CliUtils
    {
        /// <summary>
        /// Reads a demand matrix from a pickle file and converts it to pair demands.
        /// Expected pickle contents:
        /// 1) A 2D matrix-like object (list, numpy array, DataFrame values), optionally with a node order.
        /// 2) A dictionary keyed by (src, dst) tuples.
        /// </summary>
        /// <param name="pickleFilePath">Path to pickle file.</param>
        /// <param name="topology">Target topology.</param>
        /// <param name="pythonExecutable">Python executable to run unpickling helper.</param>
        /// <returns>Demand map over all node pairs in topology.</returns>
        public static Dictionary<(string, string), double> LoadDemandMatrixFromPickle(
            string pickleFilePath,
            Topology topology,
            string pythonExecutable = "python3")
        {
            if (!File.Exists(pickleFilePath))
            {
                throw new FileNotFoundException("Pickle file was not found", pickleFilePath);
            }

            var pythonScript = @"
import json
import pickle
import sys

def _to_float(v):
    try:
        return float(v)
    except Exception:
        return None

def _emit_matrix(matrix, node_order=None):
    print(json.dumps({
        'kind': 'matrix',
        'matrix': matrix,
        'node_order': node_order,
    }))

def _emit_pairs(d):
    pairs = {}
    for k, v in d.items():
        if isinstance(k, tuple) and len(k) == 2:
            fv = _to_float(v)
            if fv is not None:
                pairs[f'{k[0]}::{k[1]}'] = fv
    print(json.dumps({
        'kind': 'pair_map',
        'pair_demands': pairs,
    }))

with open(sys.argv[1], 'rb') as f:
    obj = pickle.load(f)

if isinstance(obj, dict):
    if len(obj) > 0 and all(isinstance(k, tuple) and len(k) == 2 for k in obj.keys()):
        _emit_pairs(obj)
        sys.exit(0)

    if 'pair_demands' in obj and isinstance(obj['pair_demands'], dict):
        _emit_pairs(obj['pair_demands'])
        sys.exit(0)

    candidate_matrix = None
    candidate_nodes = None
    for key in ['matrix', 'demand_matrix', 'tm', 'traffic_matrix']:
        if key in obj:
            candidate_matrix = obj[key]
            break
    for key in ['nodes', 'node_order', 'node_names', 'labels']:
        if key in obj:
            candidate_nodes = [str(x) for x in obj[key]]
            break

    if candidate_matrix is not None:
        if hasattr(candidate_matrix, 'tolist'):
            candidate_matrix = candidate_matrix.tolist()
        _emit_matrix(candidate_matrix, candidate_nodes)
        sys.exit(0)

if hasattr(obj, 'to_numpy') and hasattr(obj, 'index'):
    matrix = obj.to_numpy().tolist()
    node_order = [str(x) for x in obj.index]
    _emit_matrix(matrix, node_order)
    sys.exit(0)

if hasattr(obj, 'tolist'):
    obj = obj.tolist()

if isinstance(obj, list):
    _emit_matrix(obj, None)
    sys.exit(0)

raise RuntimeError('Unsupported pickle payload. Expected 2D matrix-like object, DataFrame, or dict keyed by (src, dst).')
";

            var processInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            processInfo.ArgumentList.Add("-");
            processInfo.ArgumentList.Add(pickleFilePath);

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                throw new Exception("Failed to start python process to parse pickle file.");
            }

            process.StandardInput.Write(pythonScript);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Failed to parse pickle demand file. Python error: {error}");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                throw new Exception("Python pickle parser returned empty output.");
            }

            var parsed = JObject.Parse(output);
            var demands = topology.GetNodePairs().ToDictionary(pair => pair, _ => 0.0);

            var kind = parsed["kind"]?.ToString();
            if (kind == "pair_map")
            {
                var pairDemands = parsed["pair_demands"] as JObject
                    ?? throw new Exception("pair_map output missing pair_demands object.");
                foreach (var prop in pairDemands.Properties())
                {
                    var parts = prop.Name.Split("::");
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var pair = (parts[0], parts[1]);
                    if (!demands.ContainsKey(pair))
                    {
                        continue;
                    }

                    var value = prop.Value.Value<double>();
                    if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    {
                        throw new Exception($"Invalid demand value {value} for pair ({pair.Item1}, {pair.Item2}).");
                    }

                    demands[pair] = value;
                }

                return demands;
            }

            if (kind != "matrix")
            {
                throw new Exception("Unsupported parsed pickle output kind.");
            }

            var matrix = parsed["matrix"] as JArray
                ?? throw new Exception("Matrix output missing matrix field.");
            var numRows = matrix.Count;
            if (numRows == 0)
            {
                return demands;
            }

            var nodeOrderToken = parsed["node_order"];
            List<string> nodeOrder;
            if (nodeOrderToken is JArray nodeOrderArray && nodeOrderArray.Count > 0)
            {
                nodeOrder = nodeOrderArray.Select(x => x?.ToString() ?? string.Empty).ToList();
            }
            else
            {
                nodeOrder = topology.GetAllNodes().ToList();
            }

            if (nodeOrder.Count != numRows)
            {
                throw new Exception($"Matrix row count ({numRows}) does not match node count ({nodeOrder.Count}).");
            }

            foreach (var row in matrix)
            {
                if (row is not JArray rowArray || rowArray.Count != nodeOrder.Count)
                {
                    throw new Exception("Demand matrix must be square and match topology node count.");
                }
            }

            var topologyNodes = new HashSet<string>(topology.GetAllNodes());
            var parsedNodes = new HashSet<string>(nodeOrder);
            if (!topologyNodes.SetEquals(parsedNodes))
            {
                throw new Exception("Node order in pickle does not match topology nodes.");
            }

            for (int i = 0; i < nodeOrder.Count; i++)
            {
                var src = nodeOrder[i];
                var row = (JArray)matrix[i];
                for (int j = 0; j < nodeOrder.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    var dst = nodeOrder[j];
                    var demandValue = row[j]?.Value<double>() ?? 0.0;
                    if (double.IsNaN(demandValue) || double.IsInfinity(demandValue) || demandValue < 0)
                    {
                        throw new Exception($"Invalid demand value {demandValue} for pair ({src}, {dst}).");
                    }

                    demands[(src, dst)] = demandValue;
                }
            }

            return demands;
        }

        /// <summary>
        /// Returns the heuristic encoder, partition and partitionlist
        /// based on inputs.
        /// </summary>
        /// TODO: it would be helpful if you say when someone should use this.
        /// TODO: modify to have a heuristic interface potentially where you can get all the parameters of the heuristic. Right now, it has parameters for multiple different heuristics lumped together?
        /// TODO: add a comment that explains what the different heuristics are.
        public static (IEncoder<TVar, TSolution>, IDictionary<(string, string), int>, IList<IDictionary<(string, string), int>>) getHeuristic<TVar, TSolution>(
                ISolver<TVar, TSolution> solver, Topology topology, Heuristic h, int numPaths, int numSlices = -1, double demandPinningThreshold = -1,
                IDictionary<(string, string), int> partition = null, int numSamples = -1, IList<IDictionary<(string, string), int>> partitionsList = null,
                double partitionSensitivity = -1, bool DirectEncoder = false, double scaleFactor = 1.0,
                InnerRewriteMethodChoice InnerEncoding = InnerRewriteMethodChoice.KKT, int maxShortestPathLen = -1)
        {
            IEncoder<TVar, TSolution> heuristicEncoder;
            switch (h)
            {
                case Heuristic.Pop:
                    Console.WriteLine("Exploring pop heuristic");
                    if (partition == null)
                    {
                        partition = topology.RandomPartition(numSlices);
                    }
                    if (DirectEncoder)
                    {
                        // TODO: what is the directencoder, and how is it different from the others.
                        // TODO: fix the code.
                        heuristicEncoder = new TEMaxFlowOptimalEncoder<TVar, TSolution>(solver, numPaths);
                        throw new Exception("should verify the above implementation...");
                    }
                    else
                    {
                        heuristicEncoder = new PopEncoder<TVar, TSolution>(solver, numPaths, numSlices, partition, partitionSensitivity: partitionSensitivity);
                    }
                    break;
                case Heuristic.DemandPinning:
                    Console.WriteLine("Exploring demand pinning heuristic");
                    if (DirectEncoder)
                    {
                        Console.WriteLine("Direct DP");
                        heuristicEncoder = new DirectDemandPinningEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold);
                    }
                    else if (InnerEncoding == InnerRewriteMethodChoice.PrimalDual)
                    {
                        // TODO: needs a comment that explains what indirect quantized DP is.
                        Console.WriteLine("Indirect Quantized DP");
                        heuristicEncoder = new DemandPinningQuantizedEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                    }
                    else
                    {
                        // TODO: needs a comment that explains what indirect DP is.
                        Console.WriteLine("Indirect DP");
                        heuristicEncoder = new DemandPinningEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                    }
                    break;
                case Heuristic.ExpectedPop:
                    if (partitionsList == null)
                    {
                        partitionsList = new List<IDictionary<(string, string), int>>();
                        for (int i = 0; i < numSamples; i++)
                        {
                            partitionsList.Add(topology.RandomPartition(numSlices));
                        }
                    }
                    Console.WriteLine("Exploring the expected pop heuristic");
                    heuristicEncoder = new ExpectedPopEncoder<TVar, TSolution>(solver, numPaths, numSamples, numSlices, partitionsList);
                    break;
                case Heuristic.PopDp:
                    Console.WriteLine("Exploring combination of POP and DP.");
                    var heuristicEncoderList = new List<IEncoder<TVar, TSolution>>();
                    if (partition == null)
                    {
                        partition = topology.RandomPartition(numSlices);
                    }
                    if (InnerEncoding == InnerRewriteMethodChoice.PrimalDual)
                    {
                        Console.WriteLine("Indirect Quantized DP");
                        var heuristicEncoder1 = new DemandPinningQuantizedEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                        heuristicEncoderList.Add(heuristicEncoder1);
                    }
                    else
                    {
                        Console.WriteLine("Indirect DP");
                        var heuristicEncoder1 = new DemandPinningEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                        heuristicEncoderList.Add(heuristicEncoder1);
                    }
                    Console.WriteLine("Adding the pop heuristic");
                    var heuristicEncoder2 = new PopEncoder<TVar, TSolution>(solver, numPaths, numSlices, partition, partitionSensitivity: partitionSensitivity);
                    heuristicEncoderList.Add(heuristicEncoder2);
                    heuristicEncoder = new TECombineHeuristicsEncoder<TVar, TSolution>(solver, heuristicEncoderList, k: numPaths);
                    break;
                case Heuristic.ExpectedPopDp:
                    Console.WriteLine("Exploring expected combination of POP and DP.");
                    var expectedPopDpEncoderList = new List<IEncoder<TVar, TSolution>>();
                    if (partition == null)
                    {
                        partition = topology.RandomPartition(numSlices);
                    }
                    if (InnerEncoding == InnerRewriteMethodChoice.PrimalDual)
                    {
                        Console.WriteLine("Indirect Quantized DP");
                        var dpEncoder = new DemandPinningQuantizedEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                        expectedPopDpEncoderList.Add(dpEncoder);
                    }
                    else
                    {
                        Console.WriteLine("Indirect DP");
                        var dpEncoder = new DemandPinningEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                        expectedPopDpEncoderList.Add(dpEncoder);
                    }
                    if (partitionsList == null)
                    {
                        partitionsList = new List<IDictionary<(string, string), int>>();
                        for (int i = 0; i < numSamples; i++)
                        {
                            partitionsList.Add(topology.RandomPartition(numSlices));
                        }
                    }
                    Console.WriteLine("Exploring the expected pop heuristic");
                    var expectedPopEncoder = new ExpectedPopEncoder<TVar, TSolution>(solver, numPaths, numSamples, numSlices, partitionsList);
                    expectedPopDpEncoderList.Add(expectedPopEncoder);
                    heuristicEncoder = new TECombineHeuristicsEncoder<TVar, TSolution>(solver, expectedPopDpEncoderList, k: numPaths);
                    break;
                case Heuristic.ParallelPop:
                    Console.WriteLine("Exploring Parallel POP.");
                    var parallelPopEncoderList = new List<IEncoder<TVar, TSolution>>();
                    if (partitionsList == null)
                    {
                        partitionsList = new List<IDictionary<(string, string), int>>();
                        for (int i = 0; i < numSamples; i++)
                        {
                            partitionsList.Add(topology.RandomPartition(numSlices));
                        }
                    }
                    for (int i = 0; i < numSamples; i++)
                    {
                        Console.WriteLine(String.Format("Adding POP instance {0}", i));
                        var popEncoder = new PopEncoder<TVar, TSolution>(solver, numPaths, numSlices, partitionsList[i], partitionSensitivity: partitionSensitivity);
                        parallelPopEncoderList.Add(popEncoder);
                    }
                    heuristicEncoder = new TECombineHeuristicsEncoder<TVar, TSolution>(solver, parallelPopEncoderList, k: numPaths);
                    break;
                case Heuristic.ParallelPopDp:
                    Console.WriteLine("Exploring Parallel POP + DP.");
                    var parallelPopDpEncoderList = new List<IEncoder<TVar, TSolution>>();
                    if (partition == null)
                    {
                        partition = topology.RandomPartition(numSlices);
                    }
                    Console.WriteLine("Adding DemandPinning.");
                    if (InnerEncoding == InnerRewriteMethodChoice.PrimalDual)
                    {
                        Console.WriteLine("Indirect Quantized DP");
                        var dpEncoder = new DemandPinningQuantizedEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                        parallelPopDpEncoderList.Add(dpEncoder);
                    }
                    else
                    {
                        Console.WriteLine("Indirect DP");
                        var dpEncoder = new DemandPinningEncoder<TVar, TSolution>(solver, numPaths, demandPinningThreshold, scaleFactor: scaleFactor);
                        parallelPopDpEncoderList.Add(dpEncoder);
                    }
                    Console.WriteLine("Adding parallel POP.");
                    if (partitionsList == null)
                    {
                        partitionsList = new List<IDictionary<(string, string), int>>();
                        for (int i = 0; i < numSamples; i++)
                        {
                            partitionsList.Add(topology.RandomPartition(numSlices));
                        }
                    }
                    for (int i = 0; i < numSamples; i++)
                    {
                        Console.WriteLine(String.Format("Adding POP instance {0}", i));
                        var popEncoder = new PopEncoder<TVar, TSolution>(solver, numPaths, numSlices, partitionsList[i], partitionSensitivity: partitionSensitivity);
                        parallelPopDpEncoderList.Add(popEncoder);
                    }
                    heuristicEncoder = new TECombineHeuristicsEncoder<TVar, TSolution>(solver, parallelPopDpEncoderList, k: numPaths);
                    break;
                case Heuristic.ModifiedDp:
                    Console.WriteLine("Exploring modified demand pinning heuristic");
                    if (DirectEncoder)
                    {
                        Console.WriteLine("Direct DP");
                        throw new Exception("Not implemented yet.");
                    }
                    else if (InnerEncoding == InnerRewriteMethodChoice.PrimalDual)
                    {
                        Console.WriteLine("Indirect Quantized DP");
                        heuristicEncoder = new ModifiedDemandPinningQuantizedEncoder<TVar, TSolution>(solver,
                                                numPaths, maxShortestPathLen, demandPinningThreshold, scaleFactor: scaleFactor);
                    }
                    else
                    {
                        Console.WriteLine("Indirect DP");
                        throw new Exception("Not implemented yet.");
                    }
                    break;
                default:
                    throw new Exception("No heuristic selected.");
            }
            return (heuristicEncoder, partition, partitionsList);
        }

        /// <summary>
        /// Get the topology and the clusters.
        /// </summary>
        /// TODO: this should be two separate functions. One should get the topology and the other the clusters.
        public static (Topology, List<Topology>) getTopology(string topologyFile, string pathFile, double downScaleFactor, bool enableClustering,
                int numClusters, string clusterDir, bool verbose)
        {
            if (string.IsNullOrWhiteSpace(pathFile))
            {
                var topologyName = Path.GetFileNameWithoutExtension(topologyFile);
                pathFile = Path.Combine("..", "Topologies", "outputs", "paths", topologyName + "_paths.json");
                Utils.logger("No path file was provided. Computed paths will be cached in " + pathFile, verbose);
            }

            Topology topology = Parser.ReadTopologyJson(topologyFile, pathFile, scaleFactor: downScaleFactor);
            List<Topology> clusters = new List<Topology>();
            if (enableClustering)
            {
                for (var cidx = 0; cidx < numClusters; cidx++)
                {
                    var path = Path.Combine(clusterDir, string.Format("cluster_{0}.json", cidx));
                    Utils.logger(String.Format("Cluster idx {0}: path = {1}", cidx, path), verbose);
                    clusters.Add(Parser.ReadTopologyJson(path, scaleFactor: downScaleFactor));
                }
            }
            return (topology, clusters);
        }

        /// <summary>
        /// Get the method from MetaOpt to find adversarial inputs.
        /// </summary>
        /// TODO:change naming, seesm like the function is solving the problem in general but yet the output is called TEoptimizationsolution ...
        /// TODO: remove unused inputs.
        public static (TEOptimizationSolution, TEOptimizationSolution) getMetaOptResult<TVar, TSolution>(
                TEAdversarialInputGenerator<TVar, TSolution> adversarialInputGenerator,
                IEncoder<TVar, TSolution> optimalEncoder,
                IEncoder<TVar, TSolution> heuristicEncoder,
                double demandUB,
                InnerRewriteMethodChoice innerEncoding,
                GenericList demandList,
                bool enableClustering,
                int clusterVersion,
                List<Topology> clusters,
                int numInterClusterSamples,
                int numNodesPerCluster,
                int numInterClusterQuantizations,
                bool simplify,
                bool verbose,
                double density,
                double LargeDemandLB,
                int LargeMaxDistance,
                int SmallMaxDistance,
                bool MetaOptRandomInitialization,
                IEncoder<TVar, TSolution> HeuisticDirectEncoder)
        {
            Utils.logger("Going to find the maximum gap directly", verbose);
            Utils.logger("Simplified Option: " + simplify, verbose);
            Utils.logger("Cluster lvl scale up: " + enableClustering, verbose);
            Utils.logger("Random Initialization: " + MetaOptRandomInitialization, verbose);
            (TEOptimizationSolution, TEOptimizationSolution) result;
            if (enableClustering)
            {
                switch (clusterVersion)
                {
                    case 1:
                        throw new Exception("density and locality not implemented yet.");
                    // result = adversarialInputGenerator.MaximizeOptimalityGapWithClusteringV1(clusters, optimalEncoder, heuristicEncoder, demandUB,
                    //         numInterClusterSamples, numNodesPerCluster, innerEncoding: innerEncoding, demandList: demandList,
                    //         simplify: simplify, verbose: verbose);
                    // break;
                    case 2:
                        result = adversarialInputGenerator.MaximizeOptimalityGapWithClusteringV2(clusters, optimalEncoder, heuristicEncoder, demandUB,
                                numInterClusterSamples, numNodesPerCluster, innerEncoding: innerEncoding, demandList: demandList,
                                simplify: simplify, verbose: verbose, density: density, LargeDemandLB: LargeDemandLB, LargeMaxDistance: LargeMaxDistance,
                                SmallMaxDistance: SmallMaxDistance, randomInitialization: MetaOptRandomInitialization, HeuisticDirectEncoder: HeuisticDirectEncoder);
                        break;
                    case 3:
                        throw new Exception("density and locality not implemented yet.");
                    // result = adversarialInputGenerator.MaximizeOptimalityGapWithClusteringV3(clusters, optimalEncoder, heuristicEncoder, demandUB,
                    //         numInterClusterSamples, numNodesPerCluster, innerEncoding, demandList: demandList,
                    //         numInterClusterQuantizations: numInterClusterQuantizations, simplify: simplify, verbose: verbose);
                    // break;
                    default:
                        throw new Exception("Cluster Version is invalid");
                }
            }
            else
            {
                Debug.Assert(!MetaOptRandomInitialization);
                result = adversarialInputGenerator.MaximizeOptimalityGap(optimalEncoder, heuristicEncoder, demandUB, innerEncoding: innerEncoding,
                        demandList: demandList, simplify: simplify, verbose: verbose, density: density, LargeDemandLB: LargeDemandLB,
                        LargeMaxDistance: LargeMaxDistance, SmallMaxDistance: SmallMaxDistance);
            }
            return result;
        }

        /// <summary>
        /// Solves the adversarial input for demand pinning heuristic
        /// with specified parameters.
        /// </summary>
        /// TODO: we need to improve this interface. ideally you shouldn't have one function per heuristic. but a general interface that you call.
        public static (double, double, IDictionary<(string, string), double>) maximizeOptimalityGapDemandPinning<TVar, TSolution>(ISolver<TVar, TSolution> solver,
            Topology topology, int numPaths, double threshold, int numProcessors)
        {
            solver.CleanAll();
            var (heuristicEncoder, _, _) = getHeuristic<TVar, TSolution>(solver, topology, Heuristic.DemandPinning,
                numPaths, demandPinningThreshold: threshold);
            var optimalEncoder = new TEMaxFlowOptimalEncoder<TVar, TSolution>(solver, numPaths);
            var adversarialInputGenerator = new TEAdversarialInputGenerator<TVar, TSolution>(topology, numPaths, numProcessors);
            (TEOptimizationSolution, TEOptimizationSolution) result =
                adversarialInputGenerator.MaximizeOptimalityGap(optimalEncoder, heuristicEncoder);
            double optimal = result.Item1.MaxObjective;
            double heuristic = result.Item2.MaxObjective;
            var demands = result.Item1.Demands;
            return (optimal, heuristic, demands);
        }

        /// <summary>
        /// get optimal and deamnd pinning total flow for a given demand.
        /// The function first encodes the demand pinning heuristic (it assumes we do not want to add any additional constraints).
        /// Then it solves the optimization and computes total amount of demand the heuristic is able satisfy.
        /// It then encodes the optimal form and solves the optimization and computes the total amount of demand the optimal is able to satisfy.
        /// The function returns (optimal demand met, heuristic demand met).
        /// </summary>
        public static (double, double) getOptimalDemandPinningTotalDemand<TVar, TSolution>(ISolver<TVar, TSolution> solver, Dictionary<(string, string), double> demands,
            Topology topology, int numPaths, double threshold)
        {
            solver.CleanAll();
            var (heuristicEncoder, _, _) = getHeuristic<TVar, TSolution>(solver, topology, Heuristic.DemandPinning,
                numPaths, demandPinningThreshold: threshold);
            var heuristicEncoding = heuristicEncoder.Encoding(topology, inputEqualityConstraints: demands, noAdditionalConstraints: true);
            var solverSolutionHeuristic = heuristicEncoder.Solver.Maximize(heuristicEncoding.MaximizationObjective);
            var optimizationSolutionHeuristic = (TEOptimizationSolution)heuristicEncoder.GetSolution(solverSolutionHeuristic);
            var heuristicDemandMet = optimizationSolutionHeuristic.MaxObjective;
            var optimalEncoder = new TEMaxFlowOptimalEncoder<TVar, TSolution>(solver, numPaths);
            var optimalEncoding = optimalEncoder.Encoding(topology, inputEqualityConstraints: demands, noAdditionalConstraints: true);
            var solverSolutionOptimal = optimalEncoder.Solver.Maximize(optimalEncoding.MaximizationObjective);
            var optimizationSolutionOptimal = (TEOptimizationSolution)optimalEncoder.GetSolution(solverSolutionOptimal);
            var optimalDemandMet = optimizationSolutionOptimal.MaxObjective;
            return (optimalDemandMet, heuristicDemandMet);
        }

        /// <summary>
        /// find interesting gap for expected pop.
        /// </summary>
        /// TODO: improve the comment for this function. What makes the gap interesting? Change the function name too to  be simpler.
        /// TODO: is this function in the right place? it seems like it should be in a different library?
        public static void findGapExpectedPopAdversarialDemandOnIndependentPartitions<TVar, TSolution>(CliOptions opts, Topology topology,
                Dictionary<(string, string), double> demands, double optimal)
        {
            var newSolver = (ISolver<TVar, TSolution>)new GurobiBinary(timeout: 3600, verbose: 0, timeToTerminateNoImprovement: 60);
            Console.WriteLine("trying on some random partitions to see the quality;");
            for (int i = 0; i < 100 - opts.NumRandom; i++)
            {
                topology.RandomPartition(opts.PopSlices);
            }
            int num_r = 10;
            double sum_r = 0;
            var randomPartitionList = new List<IDictionary<(string, string), int>>();
            for (int i = 0; i < num_r; i++)
            {
                newSolver.CleanAll();
                var newPartition = topology.RandomPartition(opts.PopSlices);
                randomPartitionList.Add(newPartition);
                var newHeuristicEncoder = new PopEncoder<TVar, TSolution>(newSolver, maxNumPaths: opts.Paths, numPartitions: opts.PopSlices, demandPartitions: newPartition);
                var encodingHeuristic = newHeuristicEncoder.Encoding(topology, demandEqualityConstraints: demands, noAdditionalConstraints: true);
                var solverSolutionHeuristic = newHeuristicEncoder.Solver.Maximize(encodingHeuristic.MaximizationObjective);
                var optimizationSolutionHeuristic = (TEOptimizationSolution)newHeuristicEncoder.GetSolution(solverSolutionHeuristic);
                var demandMet = optimizationSolutionHeuristic.MaxObjective;
                // var newOptimalEncoder = new OptimalEncoder<TVar, TSolution>(newSolver, topology, opts.Paths);
                // newHeuristicEncoder = new PopEncoder<TVar, TSolution>(newSolver, topology, k: opts.Paths, numPartitions: opts.PopSlices, demandPartitions: newPartition);
                // var nresult = adversarialInputGenerator.MaximizeOptimalityGap(newOptimalEncoder, (IEncoder<TVar, TSolution>)newHeuristicEncoder, opts.DemandUB);
                // var maxGap = nresult.Item1.TotalDemandMet - nresult.Item2.TotalDemandMet;
                // Console.WriteLine("random sample" + i + "-->" + "total demand = " + demandMet + " gap = " + (optimal - demandMet) + " max gap = " + maxGap);
                Console.WriteLine("random sample" + i + "-->" + "total demand = " + demandMet + " gap = " + (optimal - demandMet));
                sum_r += (optimal - demandMet);
            }
            // TODO: remove dead code.
            Console.WriteLine("==================== avg gap on " + num_r + " random partitions: " + (sum_r / num_r));
            // Console.WriteLine("==================== trying all-to-all demand");
            // var all2allDemand = new Dictionary<(string, string), double>();
            // double demandub = topology.MaxCapacity() * opts.Paths;
            // if (opts.DemandUB > 0) {
            //     demandub = opts.DemandUB;
            // }
            // foreach (var pair in topology.GetNodePairs()) {
            //     all2allDemand[pair] = demandub;
            // }
            // num_r = 10;
            // sum_r = 0;
            // for (int i = 0; i < num_r; i++) {
            //     newSolver.CleanAll();
            //     var newPartition = randomPartitionList[i];
            //     var newHeuristicEncoder = new PopEncoder<TVar, TSolution>(newSolver, topology, k: opts.Paths, numPartitions: opts.PopSlices, demandPartitions: newPartition);
            //     var encodingHeuristic = newHeuristicEncoder.Encoding(demandEqualityConstraints: all2allDemand, noAdditionalConstraints: true);
            //     var solverSolutionHeuristic = newHeuristicEncoder.Solver.Maximize(encodingHeuristic.MaximizationObjective);
            //     var optimizationSolutionHeuristic = newHeuristicEncoder.GetSolution(solverSolutionHeuristic);
            //     var newOptimalEncoder = new OptimalEncoder<TVar, TSolution>(newSolver, topology, opts.Paths);
            //     var optimalEncoding = newOptimalEncoder.Encoding(demandEqualityConstraints: all2allDemand, noAdditionalConstraints: true);
            //     var solverSolutionOptimal = newOptimalEncoder.Solver.Maximize(optimalEncoding.MaximizationObjective);
            //     var optimizationSolutionOptimal = newOptimalEncoder.GetSolution(solverSolutionOptimal);
            //     var demandMetHeuristic = optimizationSolutionHeuristic.TotalDemandMet;
            //     var demandMetOptimal = optimizationSolutionOptimal.TotalDemandMet;
            //     Console.WriteLine("random sample" + i + "-->" + "total demand: optimal = " + demandMetOptimal + " heuristic = " +
            //             demandMetHeuristic + " gap = " + (demandMetOptimal - demandMetHeuristic));
            //     sum_r += (demandMetOptimal - demandMetHeuristic);
            // }
            // Console.WriteLine("==================== avg gap all-to-all on " + num_r + " random partitions: " + (sum_r / num_r));
            // var distance = 2;
            // Console.WriteLine("========================= trying all nodes sending to other nodes with distance <= " + distance);
            // var distanceDemand = new Dictionary<(string, string), double>();
            // demandub = topology.MaxCapacity() * opts.Paths;
            // if (opts.DemandUB > 0) {
            //     demandub = opts.DemandUB;
            // }
            // foreach (var pair in topology.GetNodePairs()) {
            //     var shortestPaths = topology.ShortestKPaths(1, pair.Item1, pair.Item2);
            //     if (shortestPaths[0].Count() <= distance + 1) {
            //         distanceDemand[pair] = demandub;
            //     }
            // }
            // FillEmptyPairsWithZeroDemand(topology, distanceDemand);
            // num_r = 10;
            // sum_r = 0;
            // for (int i = 0; i < num_r; i++) {
            //     newSolver.CleanAll();
            //     var newPartition = randomPartitionList[i];
            //     var newHeuristicEncoder = new PopEncoder<TVar, TSolution>(newSolver, topology, k: opts.Paths, numPartitions: opts.PopSlices, demandPartitions: newPartition);
            //     var encodingHeuristic = newHeuristicEncoder.Encoding(demandEqualityConstraints: distanceDemand, noAdditionalConstraints: true);
            //     var solverSolutionHeuristic = newHeuristicEncoder.Solver.Maximize(encodingHeuristic.MaximizationObjective);
            //     var optimizationSolutionHeuristic = newHeuristicEncoder.GetSolution(solverSolutionHeuristic);
            //     var newOptimalEncoder = new OptimalEncoder<TVar, TSolution>(newSolver, topology, opts.Paths);
            //     var optimalEncoding = newOptimalEncoder.Encoding(demandEqualityConstraints: distanceDemand, noAdditionalConstraints: true);
            //     var solverSolutionOptimal = newOptimalEncoder.Solver.Maximize(optimalEncoding.MaximizationObjective);
            //     var optimizationSolutionOptimal = newOptimalEncoder.GetSolution(solverSolutionOptimal);
            //     var demandMetHeuristic = optimizationSolutionHeuristic.TotalDemandMet;
            //     var demandMetOptimal = optimizationSolutionOptimal.TotalDemandMet;
            //     Console.WriteLine("random sample" + i + "-->" + "total demand: optimal = " + demandMetOptimal + " heuristic = " +
            //             demandMetHeuristic + " gap = " + (demandMetOptimal - demandMetHeuristic));
            //     sum_r += (demandMetOptimal - demandMetHeuristic);
            // }
            // Console.WriteLine("==================== avg gap all nodes sending to other nodes with distance <= " + distance +
            //     " on " + num_r + " random partitions: " + (sum_r / num_r));
            // var numFlowPerEdge = 2;
            // Console.WriteLine("========================= trying filling all edges with  " + numFlowPerEdge + " flows!!");
            // var flowPerEdgeDemand = new Dictionary<(string, string), double>();
            // var edgeToNumFlowMapping = new Dictionary<(string, string), double>();
            // foreach (var edge in topology.GetAllEdges()) {
            //     edgeToNumFlowMapping[(edge.Source, edge.Target)] = 0;
            // }
            // demandub = topology.MaxCapacity() * opts.Paths;
            // if (opts.DemandUB > 0) {
            //     demandub = opts.DemandUB;
            // }
            // var spl = 1;
            // var allFilled = true;
            // var addedDemand = false;
            // do {
            //     addedDemand = false;
            //     Console.WriteLine("====spl= " + spl.ToString());
            //     foreach (var pair in topology.GetNodePairs()) {
            //         var shortestPaths = topology.ShortestKPaths(1, pair.Item1, pair.Item2)[0];
            //         if (shortestPaths.Count() == spl + 1) {
            //             var foundValid = false;
            //             for (int n = 0; n < spl; n++) {
            //                 if (edgeToNumFlowMapping[(shortestPaths[n], shortestPaths[n + 1])] < numFlowPerEdge) {
            //                     foundValid = true;
            //                     break;
            //                 }
            //             }
            //             if (foundValid) {
            //                 addedDemand = true;
            //                 flowPerEdgeDemand[(pair)] = demandub;
            //                 for (int n = 0; n < spl; n++) {
            //                     edgeToNumFlowMapping[(shortestPaths[n], shortestPaths[n + 1])] += 1;
            //                 }
            //             }
            //         }
            //     }
            //     spl += 1;
            //     foreach (var edge in topology.GetAllEdges()) {
            //         if (edgeToNumFlowMapping[(edge.Source, edge.Target)] < numFlowPerEdge) {
            //             allFilled = false;
            //         }
            //     }
            // } while (!allFilled & addedDemand);
            // FillEmptyPairsWithZeroDemand(topology, flowPerEdgeDemand);
            // num_r = 10;
            // sum_r = 0;
            // for (int i = 0; i < num_r; i++) {
            //     newSolver.CleanAll();
            //     var newPartition = randomPartitionList[i];
            //     var newHeuristicEncoder = new PopEncoder<TVar, TSolution>(newSolver, topology, k: opts.Paths, numPartitions: opts.PopSlices, demandPartitions: newPartition);
            //     var encodingHeuristic = newHeuristicEncoder.Encoding(demandEqualityConstraints: flowPerEdgeDemand, noAdditionalConstraints: true);
            //     var solverSolutionHeuristic = newHeuristicEncoder.Solver.Maximize(encodingHeuristic.MaximizationObjective);
            //     var optimizationSolutionHeuristic = newHeuristicEncoder.GetSolution(solverSolutionHeuristic);
            //     var newOptimalEncoder = new OptimalEncoder<TVar, TSolution>(newSolver, topology, opts.Paths);
            //     var optimalEncoding = newOptimalEncoder.Encoding(demandEqualityConstraints: flowPerEdgeDemand, noAdditionalConstraints: true);
            //     var solverSolutionOptimal = newOptimalEncoder.Solver.Maximize(optimalEncoding.MaximizationObjective);
            //     var optimizationSolutionOptimal = newOptimalEncoder.GetSolution(solverSolutionOptimal);
            //     var demandMetHeuristic = optimizationSolutionHeuristic.TotalDemandMet;
            //     var demandMetOptimal = optimizationSolutionOptimal.TotalDemandMet;
            //     Console.WriteLine("random sample" + i + "-->" + "total demand: optimal = " + demandMetOptimal + " heuristic = " +
            //             demandMetHeuristic + " gap = " + (demandMetOptimal - demandMetHeuristic));
            //     sum_r += (demandMetOptimal - demandMetHeuristic);
            // }
            // Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(edgeToNumFlowMapping, Newtonsoft.Json.Formatting.Indented));
            // Console.WriteLine("==================== avg gap all fix " + numFlowPerEdge + " flow per edge demands" +
            //     " on " + num_r + " random partitions: " + (sum_r / num_r));
        }
    }
}