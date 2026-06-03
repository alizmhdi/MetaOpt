// <copyright file="Program.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace MetaOptimize.Cli
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using CommandLine;
    using Gurobi;
    using MetaOptimize;
    using ZenLib;

    /// <summary>
    /// Main entry point for the program.
    /// </summary>
    public class MainEntry
    {
        /// <summary>
        /// checks whether we get the solution we expect after running the solvers.
        /// </summary>
        /// <param name="args"></param>
        public static void TEExampleMain(string[] args)
        {
            var topology = new Topology();
            topology.AddNode("a");
            topology.AddNode("b");
            topology.AddNode("c");
            topology.AddNode("d");
            topology.AddEdge("a", "b", capacity: 10);
            topology.AddEdge("a", "c", capacity: 10);
            topology.AddEdge("b", "d", capacity: 10);
            topology.AddEdge("c", "d", capacity: 10);

            var partition = topology.RandomPartition(2);
            var solverG = new GurobiSOS();
            var optimalEncoderG = new TEMaxFlowOptimalEncoder<GRBVar, GRBModel>(solverG, maxNumPaths: 1);
            var popEncoderG = new PopEncoder<GRBVar, GRBModel>(solverG, maxNumPaths: 1, numPartitions: 2, demandPartitions: partition);
            var adversarialInputGenerator = new TEAdversarialInputGenerator<GRBVar, GRBModel>(topology, maxNumPaths: 1);

            var (optimalSolutionG, popSolutionG) = adversarialInputGenerator.MaximizeOptimalityGap(optimalEncoderG, popEncoderG);

            Console.WriteLine("Optimal:");
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(optimalSolutionG, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine("****");
            Console.WriteLine("Heuristic:");
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(popSolutionG, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine("****");

            var optimal = optimalSolutionG.MaxObjective;
            var heuristic = popSolutionG.MaxObjective;
            Console.WriteLine($"optimalG={optimal}, heuristicG={heuristic}");

            var demands = new Dictionary<(string, string), double>(optimalSolutionG.Demands);
            var optGSolver = new GurobiSOS();
            optimalEncoderG = new TEMaxFlowOptimalEncoder<GRBVar, GRBModel>(optGSolver, maxNumPaths: 1);
            var popGSolver = new GurobiSOS();
            popEncoderG = new PopEncoder<GRBVar, GRBModel>(popGSolver, maxNumPaths: 1, numPartitions: 2, demandPartitions: partition);
            Utils.checkSolution(topology, popEncoderG, optimalEncoderG, heuristic, optimal, demands, "gurobiCheck");
        }

        /// <summary>
        /// Use this function to test our theorem for VBP.
        /// (see theorem 1 in our NSDI24 Paper).
        /// </summary>
        public static void vbpMain(string[] args)
        {
            // OPT = 2m + 3n
            // HUE = 4m + 6n
            // num jobs = 6m + 9n
            var solverG = new GurobiSOS(verbose: 0);

            for (int m = 1; m <= 1; m++)
            {
                for (int n = 1; n <= 1; n++)
                {
                    Console.WriteLine(String.Format("============ m = {0}, n = {1}", m, n));
                    var binSize = new List<double>();
                    binSize.Add(1.0001);
                    binSize.Add(1.0001);
                    var bins = new Bins(4 * m + 6 * n, binSize);
                    // TODO: need to change var name to be appropriate for the problem.
                    var demands = new Dictionary<int, List<double>>();
                    int nxt_key = 0;
                    demands[nxt_key] = new List<double>() { 1.0, 0.42 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.52, 0.24 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.5, 0.3 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.3, 0.2 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.3, 0.14 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.08, 0.58 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.0, 0.6 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.0, 0.12 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.0, 0.58 };
                    nxt_key += 1;
                    demands[nxt_key] = new List<double>() { 0.0, 0.58 };

                    solverG.CleanAll();
                    var optimalEncoder = new VBPOptimalEncoder<GRBVar, GRBModel>(solverG, demands.Count, demands[0].Count);
                    var optimalEncoding = optimalEncoder.Encoding(bins, inputEqualityConstraints: demands, verbose: false);
                    var solverSolutionOptimal = optimalEncoder.Solver.Maximize(optimalEncoding.MaximizationObjective);
                    var optimizationSolutionOptimal = (VBPOptimizationSolution)optimalEncoder.GetSolution(solverSolutionOptimal);
                    Console.WriteLine(
                        String.Format("===== OPT {0}", optimizationSolutionOptimal.TotalNumBinsUsed));

                    solverG.CleanAll();
                    var ffdEncoder = new FFDItemCentricEncoder<GRBVar, GRBModel>(solverG, demands.Count, demands[0].Count);
                    var ffdEncoding = ffdEncoder.Encoding(bins, inputEqualityConstraints: demands, verbose: false);
                    var solverSolutionFFD = optimalEncoder.Solver.Maximize(ffdEncoding.MaximizationObjective);
                    var solutionFFD = (VBPOptimizationSolution)ffdEncoder.GetSolution(solverSolutionFFD);
                    Console.WriteLine(
                        String.Format("===== HUE {0}", solutionFFD.TotalNumBinsUsed));
                }
            }
        }

        /// <summary>
        /// Uses VBPAdversarialInputGenerator to find adversarial inputs for VBP
        /// given the number of jobs and number of resource dimensions.
        /// </summary>
        /// <param name="numJobs">Total number of items/jobs to pack.</param>
        /// <param name="numDimensions">Number of resource dimensions per item.</param>
        public static void vbpAdversarialMain(int numJobs, int numDimensions)
        {
            int numBins = numJobs;
            double timeout = 300;
            string logDir = Path.Combine("..", "logs", "vbp_adversarial", $"jobs_{numJobs}_dims_{numDimensions}" + Utils.GetFID());
            Directory.CreateDirectory(logDir);
            string progressFile = Path.Combine(logDir, "progress.txt");
            string resultFile = Path.Combine(logDir, "result.txt");
            var solverG = new GurobiSOS(timeout: timeout, verbose: 1, recordProgress: true, logPath: progressFile);

            var binSize = new List<double>();
            for (int d = 0; d < numDimensions; d++)
            {
                binSize.Add(1.00001);
            }
            var bins = new Bins(numBins, binSize);

            // Optionally, you could pass logPath/progress to the solver if supported
            // (not all solvers/encoders may support this, but for consistency)

            var optimalEncoder = new VBPOptimalEncoder<GRBVar, GRBModel>(solverG, numJobs, numDimensions);
            var ffdEncoder = new FFDItemCentricEncoder<GRBVar, GRBModel>(solverG, numJobs, numDimensions);

            var adversarialGenerator = new VBPAdversarialInputGenerator<GRBVar, GRBModel>(bins, numJobs, numDimensions);
            var (optimalSolution, heuristicSolution) = adversarialGenerator.MaximizeOptimalityGapFFD(
                optimalEncoder,
                ffdEncoder,
                numBinsUsedOptimal: -1,
                ffdMethod: FFDMethodChoice.FFD,
                verbose: true);

            // Save results to file
            using (var writer = new StreamWriter(resultFile, false))
            {
                writer.WriteLine($"OPT bins used: {optimalSolution.TotalNumBinsUsed}");
                writer.WriteLine($"FFD bins used: {heuristicSolution.TotalNumBinsUsed}");
                writer.WriteLine($"Gap: {heuristicSolution.TotalNumBinsUsed - optimalSolution.TotalNumBinsUsed}");
                writer.WriteLine("Adversarial item sizes:");
                writer.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(optimalSolution.Items, Newtonsoft.Json.Formatting.Indented));
            }

            // Also print to console
            Console.WriteLine($"===== OPT bins used: {optimalSolution.TotalNumBinsUsed}");
            Console.WriteLine($"===== FFD bins used: {heuristicSolution.TotalNumBinsUsed}");
            Console.WriteLine($"===== Gap: {heuristicSolution.TotalNumBinsUsed - optimalSolution.TotalNumBinsUsed}");
            Console.WriteLine($"Results saved to: {resultFile}");
            Console.WriteLine("Adversarial item sizes:");
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(optimalSolution.Items, Newtonsoft.Json.Formatting.Indented));
        }

        /// <summary>
        /// test MetaOpt on VBP.
        /// </summary>
        /// TODO: specify how this function is different from the previous.
        public static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0].Equals("solve-pkl-b4", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    throw new ArgumentException("Usage: solve-pkl-b4 <demand_matrix.pkl> [pinningThreshold] [pythonExecutable] [topologyJson]");
                }

                var picklePath = args[1];
                var threshold = args.Length >= 3
                    ? double.Parse(args[2], CultureInfo.InvariantCulture)
                    : 250.0;
                var pythonExecutable = args.Length >= 4 ? args[3] : "/home/yizhuoliang/miniconda3/envs/metarl/bin/python3";
                var topologyPath = args.Length >= 5 ? args[4] : @"../Topologies/b4-teavar.json";
                var pathFile = @"../Topologies/outputs/paths/b4-teavar_paths.json";

                SolveB4DemandPinningAndOptimalFromPickle(picklePath, threshold, pythonExecutable, topologyPath, pathFile);
                return;
            }
            // vbpMain(args);
            MainTE(args);
        }

        /// <summary>
        /// Loads a pickle demand matrix and computes demand pinning and optimal objective on B4 with 4 paths.
        /// </summary>
        /// <param name="picklePath">Path to demand matrix pickle.</param>
        /// <param name="demandPinningThreshold">Demand pinning threshold.</param>
        /// <param name="pythonExecutable">Python executable for pickle parsing.</param>
        /// <param name="topologyPath">Topology json path (defaults to B4).</param>
        /// <param name="pathFile">Path file json path (defaults to B4).</param>
        public static void SolveB4DemandPinningAndOptimalFromPickle(
            string picklePath,
            double demandPinningThreshold = 250.0,
            string pythonExecutable = "python3",
            string topologyPath = @"../Topologies/b4-teavar.json",
            string pathFile = @"../Topologies/outputs/paths/b4-teavar_paths.json")
        {
            const int numPaths = 4;
            string clusterDir = null;
            var (topology, clusters) = CliUtils.getTopology(topologyPath, pathFile, 1, false,
                                                    1, clusterDir, false);
            var demands = CliUtils.LoadDemandMatrixFromPickle(picklePath, topology, pythonExecutable);

            var solver = new GurobiSOS(verbose: 1);
            var (optimalDemandMet, demandPinningDemandMet) = CliUtils.getOptimalDemandPinningTotalDemand<GRBVar, GRBModel>(
                solver,
                demands,
                topology,
                numPaths,
                demandPinningThreshold);

            Console.WriteLine("============================================");
            Console.WriteLine("B4 fixed-demand evaluation (k=4)");
            Console.WriteLine($"Demand pickle: {picklePath}");
            Console.WriteLine($"Pinning threshold: {demandPinningThreshold}");
            Console.WriteLine($"Optimal demand met: {optimalDemandMet}");
            Console.WriteLine($"Demand pinning demand met: {demandPinningDemandMet}");
            Console.WriteLine($"Gap (optimal - pinning): {optimalDemandMet / demandPinningDemandMet}");
            Console.WriteLine("============================================");
        }

        /// <summary>
        /// test case for TE with realistic constraints.
        /// </summary>
        public static void impactOfDPThresholdOnGap()
        {
            var topologies = new Dictionary<string, string>();
            topologies["B4"] = @"../Topologies/b4-teavar.json";
            // topologies["SWAN"] = @"../Topologies/swan.json";
            // topologies["Abilene"] = @"../Topologies/abilene.json";
            Heuristic heuristicName = Heuristic.DemandPinning;
            string logDir = @"../logs/demand_pinning_sweep_thresh/" + Utils.GetFID() + @"\";
            double timeToTerminate = 5000;
            int numPaths = 4;
            double start = 5;
            double step = 2.5;
            double end = 5;
            int numProcessors = 32;

            ISolver<GRBVar, GRBModel> solver = (ISolver<GRBVar, GRBModel>)new GurobiSOS(verbose: 1, timeToTerminateNoImprovement: timeToTerminate);

            // goes through topologies one by one and sweeps through the threshold.
            foreach (var (topoName, topoPath) in topologies)
            {
                var topology = Parser.ReadTopologyJson(topoPath);
                var maxThreshold = topology.MinCapacity();
                string logFile = topoName + @"_" + heuristicName + ".txt";
                // Utils.CreateFile(logDir, logFile, removeIfExist: true);
                // Utils.AppendToFile(logDir, logFile, maxThreshold.ToString());
                for (double i = start; i <= end; i += step) {
                    var threshold = i * maxThreshold / 100;
                    var (optimal, heuristic, demands) = CliUtils.maximizeOptimalityGapDemandPinning<GRBVar, GRBModel>(
                            solver: solver, topology: topology, numPaths: numPaths, threshold: threshold, numProcessors: numProcessors);
                    var gap = optimal - heuristic;
                    // Utils.AppendToFile(logDir, logFile, i + ", " + threshold + ", " + optimal + ", " + heuristic + ", " + gap);
                    Console.WriteLine("==== Gap --> " + " i=" + i + " threshold=" + threshold + " optimal=" + optimal + " heuristic=" + heuristic + " gap=" + gap);
                }
            }
        }
        /// <summary>
        ///     test MetaOpt on TE with realistic constraints.
        /// </summary>
        public static void MainTE(string[] args)
        {
            // topo parameters
            string topoName = "b4";
            bool metaOptRndInit = false;
            string topoPath = "";
            string clusterDir = null;
            string pathFile = null;
            int numClusters = 1;
            double downScaleFactor = 1;
            bool enableClustering = false;
            int clusterVersion = -1;
            double perClusterTimeout = 0;
            if (topoName == "Cogentco")
            {
                topoPath = @"../Topologies/Cogentco.json";
                clusterDir = @"../Topologies/partition_log/Cogentco_10_fm_partitioning/";
                pathFile = @"../Topologies/outputs/paths/Cogentco_sp.json";
                numClusters = 10;
                downScaleFactor = 0.001;
                enableClustering = true;
                clusterVersion = 2;
                perClusterTimeout = 1200;
            }
            else if (topoName == "b4")
            {
                topoPath = @"../Topologies/b4-teavar.json";
                pathFile = @"../Topologies/outputs/paths/b4-teavar_paths.json";
                perClusterTimeout = 5000;
            }
            else if (topoName == "swan")
            {
                topoPath = @"../Topologies/swan.json";
                pathFile = @"../Topologies/outputs/paths/swan_paths.json";
                perClusterTimeout = 400;
            }
            else if (topoName == "abilene")
            {
                topoPath = @"../Topologies/abilene.json";
                pathFile = @"../Topologies/outputs/paths/abilene_paths.json";
                perClusterTimeout = 400;
            }
            else if (topoName == "Uninet2010")
            {
                topoPath = @"../Topologies/Uninet2010.json";
                clusterDir = @"../Topologies/partition_log/Uninet2010_8_fm_partitioning/";
                pathFile = @"../Topologies/outputs/paths/Uninet2010_sp.json";
                numClusters = 8;
                downScaleFactor = 0.001;
                enableClustering = true;
                clusterVersion = 2;
                perClusterTimeout = 1200;
                // perClusterTimeout = 27691;
            }
            else
            {
                throw new Exception("no valid topo name");
            }
            var (topology, clusters) = CliUtils.getTopology(topoPath, pathFile, downScaleFactor, enableClustering,
                                                    numClusters, clusterDir, false);
            var avgLinkCap = Math.Round(topology.AverageCapacity(), 4);
            int numPaths = 4;
            // solver parameters
            int numThreads = MachineStat.numThreads;
            int numProcessors = MachineStat.numProcessors;
            // hueristic parameters
            var heuristicName = Heuristic.DemandPinning;
            var innerEncoding = InnerRewriteMethodChoice.PrimalDual;
            // var innerEncoding = InnerRewriteMethodChoice.KKT;
            // dp variables
            var demandUBRatio = 0.5;
            var demandPinningRatio = 0.05;
            // pop variables
            int numSlices = 1;
            int numSamples = 1;
            var partition = topology.RandomPartition(numSlices);
            var partitionsList = new List<IDictionary<(string, string), int>>();
            for (int i = 0; i < numSamples; i++)
            {
                partitionsList.Add(topology.RandomPartition(numSlices));
            }
            // realistic parameters
            double density = 1;
            List<int> maxLargeDistanceList = new List<int>() { 4 };
            var maxSmallDistanceList = new List<int>() { -1 };
            double largeDemandLB = 0.25 * avgLinkCap;
            double largeDemandLB2 = largeDemandLB - 0.001;
            int verbose = 1;

            // computing gap
            var demandPinningThreshold = Math.Round(demandPinningRatio * avgLinkCap, 4);
            var demandUB = demandUBRatio * avgLinkCap;
            Console.WriteLine(
                String.Format("======== avg link cap {0}, demand UB {1}, demandThresh {2}", avgLinkCap, demandUB, demandPinningThreshold));

            var demandSet = new HashSet<double>();
            demandSet.Add(0);
            if (heuristicName == Heuristic.DemandPinning)
            {
                demandSet.Add(demandPinningThreshold);
            }
            demandSet.Add(largeDemandLB2);
            demandSet.Add(demandUB);
            var demandList = new GenericList(demandSet);
            // GenericList demandList = null;
            // Primal-Dual
            string logDir = @"../logs/realistic_constraints/" + topoName + "_" + numClusters + "_" + heuristicName
                    + "_" + demandUB + "_" + demandPinningThreshold + "_" + numPaths + "_";
            logDir = logDir + Utils.GetFID() + @"/";
            string gapFile = @"gap.txt";
            Utils.CreateFile(logDir, gapFile, removeIfExist: false);

            foreach (var maxLargeDistance in maxLargeDistanceList)
            {
                foreach (var maxSmallDistance in maxSmallDistanceList)
                {
                    Console.WriteLine(
                        String.Format("=================== maxLargeDistance {0}, maxSmallDistance {1}, LargeDemandLB {2}", maxLargeDistance, maxSmallDistance, largeDemandLB));
                    string dirname = "primal_dual_" + heuristicName + "_density_" + density + "_maxLargeDistance_"
                            + maxLargeDistance + "_maxSmallDistance" + maxSmallDistance + "_LargeDemandLB_" + largeDemandLB + "/";
                    string demandFile = dirname + @"demands.txt";
                    string progressFile = dirname + @"progress.txt";
                    ISolver<GRBVar, GRBModel> solver = (ISolver<GRBVar, GRBModel>)new GurobiSOS(perClusterTimeout, verbose, numThreads, recordProgress: true,
                                                        logPath: Path.Combine(logDir, progressFile), focusBstBd: false);
                    var (heuristicEncoder, _, _) = CliUtils.getHeuristic<GRBVar, GRBModel>(solver: solver, topology: topology,
                                                    h: heuristicName, numPaths: numPaths, numSlices: numSlices, demandPinningThreshold: demandPinningThreshold,
                                                    partition: partition, numSamples: numSamples, partitionsList: partitionsList, InnerEncoding: innerEncoding,
                                                    scaleFactor: downScaleFactor);
                    var optimalEncoder = new TEMaxFlowOptimalEncoder<GRBVar, GRBModel>(solver, numPaths);
                    var adversarialInputGenerator = new TEAdversarialInputGenerator<GRBVar, GRBModel>(topology, numPaths, numProcessors);
                    (TEOptimizationSolution, TEOptimizationSolution) result = CliUtils.getMetaOptResult(adversarialInputGenerator, optimalEncoder, heuristicEncoder,
                            demandUB, innerEncoding, demandList, enableClustering, clusterVersion, clusters, -1, -1, -1, false, false, density, largeDemandLB,
                            maxLargeDistance, maxSmallDistance, metaOptRndInit, null);
                    double optimal = result.Item1.MaxObjective;
                    double heuristic = result.Item2.MaxObjective;
                    var gap = optimal - heuristic;
                    Utils.writeDemandsToFile(Path.Combine(logDir, demandFile), result.Item1.Demands);
                    Utils.AppendToFile(logDir, gapFile,
                                    String.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}", topoName, heuristicName, numClusters, numPaths,
                                                    perClusterTimeout, demandPinningRatio, numSlices, numSamples, demandUB, density, largeDemandLB,
                                                    maxLargeDistance, maxSmallDistance, numThreads, gap));
                    Console.WriteLine("==== PrimalDual --> " + " gap=" + gap + " optimal=" + optimal + " heuristic=" + heuristic);
                }
            }
        }
        /// <summary>
        /// test case for SP-PIFO.
        /// </summary>
        public static void PIFOTestMain(string[] args)
        {
            int maxRank = 8;
            int numPackets = 18;
            int numQueues = 2;
            // int splitQueue = 2;
            // int splitRank = 5;
            var solverG = new GurobiSOS(verbose: 0);

            var packetRankEqualityConstraint = new Dictionary<int, int>();
            packetRankEqualityConstraint[0] = 0;
            packetRankEqualityConstraint[1] = 0;
            packetRankEqualityConstraint[2] = 8;
            packetRankEqualityConstraint[3] = 7;
            packetRankEqualityConstraint[4] = 7;
            packetRankEqualityConstraint[5] = 7;
            packetRankEqualityConstraint[6] = 7;
            packetRankEqualityConstraint[7] = 7;
            packetRankEqualityConstraint[8] = 7;
            packetRankEqualityConstraint[9] = 7;
            packetRankEqualityConstraint[10] = 7;
            packetRankEqualityConstraint[11] = 0;
            packetRankEqualityConstraint[12] = 0;
            packetRankEqualityConstraint[13] = 0;
            packetRankEqualityConstraint[14] = 0;
            packetRankEqualityConstraint[15] = 0;
            packetRankEqualityConstraint[16] = 0;
            packetRankEqualityConstraint[17] = 0;
            solverG.CleanAll();
            var optimalEncoder = new PIFOAvgDelayOptimalEncoder<GRBVar, GRBModel>(solverG, numPackets, maxRank);
            var optimalEncoding = optimalEncoder.Encoding(rankEqualityConstraints: packetRankEqualityConstraint);
            var solverSolutionOptimal = optimalEncoder.Solver.Maximize(optimalEncoding.MaximizationObjective);
            var optimizationSolutionOptimal = (PIFOOptimizationSolution)optimalEncoder.GetSolution(solverSolutionOptimal);
            Console.WriteLine("Optimal:");
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(optimizationSolutionOptimal, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine("****");
            Console.WriteLine("===== OPT {0}", optimizationSolutionOptimal.Cost);

            solverG.CleanAll();
            var heuristicEncoder = new SPPIFOAvgDelayEncoder<GRBVar, GRBModel>(solverG, numPackets, numQueues, maxRank);
            var heuristicEncoding = heuristicEncoder.Encoding(rankEqualityConstraints: packetRankEqualityConstraint);
            var solverSolutionHeuristic = optimalEncoder.Solver.Maximize(heuristicEncoding.MaximizationObjective);
            var solutionHeuristic = (PIFOOptimizationSolution)heuristicEncoder.GetSolution(solverSolutionHeuristic);
            Console.WriteLine("Heuristic:");
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(solutionHeuristic, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine("****");
            Console.WriteLine("===== HUE {0}", solutionHeuristic.Cost);
        }

        /// <summary>
        /// test MetaOpt on PIFO.
        /// </summary>
        public static void PIFOMain(string[] args)
        {
            int maxRank = 8;
            int numPackets = 36;
            int numQueues = 4;
            // int splitQueue = 2;
            // int splitRank = 4;
            // int maxQueueSize = 12;
            // int windowSize = 12;
            // double burstParam = 0.1;

            var targetGapThreshold = double.NaN;
            if (args != null && args.Length >= 2 && args[0].Equals("--pifo-gap-threshold", StringComparison.OrdinalIgnoreCase))
            {
                targetGapThreshold = double.Parse(args[1], CultureInfo.InvariantCulture);
            }

            var pifoProgressBasePath = Path.Combine("..", "logs", "pifo", "progress.txt");

            var solverG = new GurobiSOS(verbose: 1, timeout: 120, numThreads: 32, recordProgress: true, logPath: pifoProgressBasePath);
            var optimalEncoder = new PIFOAvgDelayOptimalEncoder<GRBVar, GRBModel>(solverG, numPackets, maxRank);
            var heuristicEncoder = new SPPIFOAvgDelayEncoder<GRBVar, GRBModel>(solverG, numPackets, numQueues, maxRank);
            // var optimalEncoder = new SPPIFOAvgDelayEncoder<GRBVar, GRBModel>(solverG, numPackets, numQueues, maxRank);
            // var heuristicEncoder = new ModifiedSPPIFOAvgDelayEncoder<GRBVar, GRBModel>(solverG, numPackets, splitQueue, numQueues,
            //     splitRank, maxRank);
            // var optimalEncoder = new PIFOWithDropAvgDelayEncoder<GRBVar, GRBModel>(solverG, numPackets, maxRank, maxQueueSize);
            // var H1 = new SPPIFOWithDropAvgDelayEncoder<GRBVar, GRBModel>(solverG, numPackets, numQueues, maxRank, maxQueueSize);
            // var H2 = new AIFOAvgDelayEncoder<GRBVar, GRBModel>(solverG, numPackets, maxRank, maxQueueSize, windowSize, burstParam);

            var adversarialGenerator = new PIFOAdversarialInputGenerator<GRBVar, GRBModel>(numPackets, maxRank);
            var (optimalSolutionG, heuristicSolutionG) = adversarialGenerator.MaximizeOptimalityGap(optimalEncoder,
                heuristicEncoder, verbose: true);
            Console.WriteLine("Optimal:");
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(optimalSolutionG, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine("****");
            Console.WriteLine("Heuristic:");
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(heuristicSolutionG, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine("****");
            Console.WriteLine("Optimal cost: " + optimalSolutionG.Cost);
            Console.WriteLine("Heuristic cost: " + heuristicSolutionG.Cost);

            if (!double.IsNaN(targetGapThreshold))
            {
                var progressPath = FindLatestProgressFilePath(pifoProgressBasePath);
                if (progressPath == null)
                {
                    Console.WriteLine("Could not find progress log file for PIFO run.");
                }
                else
                {
                    if (TryGetFirstThresholdHit(progressPath, targetGapThreshold, out var firstHitTimeMs, out var firstHitGap))
                    {
                        Console.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "First point reaching gap threshold {0}: time_ms={1}, gap={2}",
                                targetGapThreshold,
                                firstHitTimeMs,
                                firstHitGap));
                    }
                    else
                    {
                        Console.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Gap threshold {0} was not reached in this run. Progress log: {1}",
                                targetGapThreshold,
                                progressPath));
                    }
                }
            }

            var orderToRankOpt = new Dictionary<int, double>();
            var orderToRankHeu = new Dictionary<int, double>();
            for (int pid = 0; pid < numPackets; pid++) {
                if (optimalSolutionG.Admit[pid] == 1) {
                    orderToRankOpt[optimalSolutionG.Order[pid]] = optimalSolutionG.Ranks[pid];
                }
                if (heuristicSolutionG.Admit[pid] == 1) {
                    orderToRankHeu[heuristicSolutionG.Order[pid]] = heuristicSolutionG.Ranks[pid];
                }
            }

            int numInvOpt = 0;
            int numInvHeu = 0;
            for (int pid = 0; pid < numPackets; pid++)
            {
                numInvOpt += ComputeInversionNum(optimalSolutionG, orderToRankOpt, pid);
                numInvHeu += ComputeInversionNum(heuristicSolutionG, orderToRankHeu, pid);
            }
            Console.WriteLine("number of inversions in OPT: " + numInvOpt);
            Console.WriteLine("number of inversions in HEU: " + numInvHeu);
        }

        private static bool TryGetFirstThresholdHit(string progressPath, double threshold, out double timeMs, out double gap)
        {
            timeMs = -1;
            gap = double.NaN;
            foreach (var line in File.ReadLines(progressPath))
            {
                var parts = line.Split(",");
                if (parts.Length < 2)
                {
                    continue;
                }

                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var currTimeMs))
                {
                    continue;
                }

                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var currGap))
                {
                    continue;
                }

                if (currGap >= threshold)
                {
                    timeMs = currTimeMs;
                    gap = currGap;
                    return true;
                }
            }

            return false;
        }

        private static string FindLatestProgressFilePath(string baseLogPath)
        {
            var dirname = Path.GetDirectoryName(baseLogPath);
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(baseLogPath);
            var extension = Path.GetExtension(baseLogPath);
            if (string.IsNullOrEmpty(dirname) || !Directory.Exists(dirname))
            {
                return null;
            }

            var pattern = fileNameWithoutExt + "_*" + extension;
            var files = Directory.GetFiles(dirname, pattern);
            if (files == null || files.Length == 0)
            {
                return null;
            }

            string latestFile = null;
            DateTime latestWriteTime = DateTime.MinValue;
            foreach (var file in files)
            {
                var writeTime = File.GetLastWriteTimeUtc(file);
                if (writeTime > latestWriteTime)
                {
                    latestWriteTime = writeTime;
                    latestFile = file;
                }
            }

            return latestFile;
        }

        private static int ComputeInversionNum(PIFOOptimizationSolution optimalSolutionG, Dictionary<int, double> orderToRankOpt, int pid)
        {
            int numInvOpt = 0;
            if (optimalSolutionG.Admit[pid] >= 0.98)
            {
                int currOrder = optimalSolutionG.Order[pid];
                for (int prev = 0; prev < currOrder; prev++)
                {
                    if (orderToRankOpt[prev] > optimalSolutionG.Ranks[pid])
                    {
                        numInvOpt += 1;
                    }
                }
            }
            else
            {
                foreach (var (order, rank) in orderToRankOpt)
                {
                    if (rank > optimalSolutionG.Ranks[pid])
                    {
                        numInvOpt += 1;
                    }
                }
            }

            return numInvOpt;
        }

        /// <summary>
        /// Experiments for NSDI.
        /// </summary>
        public static void NSDIMain(string[] args)
        {
            // NSDIExp.compareGapDelayDiffMethodsDP();
            // NSDIExp.compareLargeScaleGapDelayDiffMethodsDP();
            // NSDIExp.compareGapDelayDiffMethodsPop();
            // NSDIExp.AblationStudyClusteringOnDP();
            // NSDIExp.BlackBoxParameterTunning();
            NSDIExp.AddRealisticConstraintsDP();
            // NSDIExp.gapThresholdDemandPinningForDifferentTopologies();
            // NSDIExp.ImpactNumPathsPartitionsExpectedPop();
            // NSDIExp.AblationStudyClusteringOnDP();
            // NSDIExp.BlackBoxParameterTunning();
            // NSDIExp.AnalyzeModifiedDP();
            // NSDIExp.ImpactNumNodesRadixSmallWordTopoDemandPinning();
            // NSDIExp.ImpactNumSamplesExpectedPop();
            // NSDIExp.AnalyzeParallelHeuristics();
        }

        /// <summary>
        /// Experiments for hotnets.
        /// </summary>
        public static void hotnetsMain(string[] args)
        {
            // var topology = Topology.RandomRegularGraph(8, 7, 1, seed: 0);
            // var topology = Topology.SmallWordGraph(5, 4, 1);
            // foreach (var edge in topology.GetAllEdges()) {
            //     Console.WriteLine(edge.Source + "_" + edge.Target);
            // }
            // foreach (var pair in topology.GetNodePairs()) {
            //     if (!topology.ContaintsEdge(pair.Item1, pair.Item2, 1)) {
            //         Console.WriteLine("missing link " + pair.Item1 + " " + pair.Item2);
            //     }
            // }
            // Experiment.printPaths();
            // HotNetsExperiment.impactOfDPThresholdOnGap();
            // Experiment.ImpactNumPathsDemandPinning();
            // Experiment.ImpactNumNodesRadixRandomRegularGraphDemandPinning();
            HotNetsExperiment.impactSmallWordGraphParamsDP();
            // Experiment.ImpactNumPathsPartitionsPop();
            // Experiment.compareGapDelayDiffMethodsPop();
            // Experiment.compareGapDelayDiffMethodsDP();
            // Experiment.compareTopoSizeLatency();
        }

        /// <summary>
        /// Main entry point for the program.
        /// The function takes the command line arguments and stores them in a
        /// static instance property of the CliOptions class.
        /// It then reads the topology and clusters from the files specified in the
        /// command line arguments and then proceeds to find the optimality gap.
        /// </summary>
        /// <param name="args">The arguments.</param>
        public static void ssMain(string[] args)
        {
            // read the command line arguments.
            var opts = CommandLine.Parser.Default.ParseArguments<CliOptions>(args).MapResult(o => o, e => null);
            CliOptions.Instance = opts;

            if (opts == null)
            {
                Environment.Exit(0);
            }

            // read the topology and clusters.
            var (topology, clusters) = CliUtils.getTopology(opts.TopologyFile, opts.PathFile, opts.DownScaleFactor, opts.EnableClustering,
                                            opts.NumClusters, opts.ClusterDir, opts.Verbose);

            getSolverAndRunNetwork(topology, clusters);
        }

        // TODO: this function is missing proper commenting
        private static void getSolverAndRunNetwork(Topology topology, List<Topology> clusters)
        {
            var opts = CliOptions.Instance;
            // use the Z3 solver via the Zen wrapper library.
            switch (opts.SolverChoice)
            {
                case SolverChoice.Zen:
                    // run the zen optimizer.
                    RunNetwork(new SolverZen(), topology, clusters);
                    break;
                case SolverChoice.Gurobi:
                    var storeProgress = opts.StoreProgress & (opts.Method == MethodChoice.Direct);
                    if (opts.Heuristic == Heuristic.DemandPinning)
                    {
                        RunNetwork(new GurobiSOS(opts.Timeout, Convert.ToInt32(opts.Verbose),
                                                    timeToTerminateNoImprovement: opts.TimeToTerminateIfNoImprovement,
                                                    numThreads: opts.NumGurobiThreads,
                                                    recordProgress: storeProgress,
                                                    logPath: opts.LogFile),
                                topology, clusters);
                    }
                    else
                    {
                        RunNetwork(new GurobiSOS(opts.Timeout, Convert.ToInt32(opts.Verbose),
                                                    timeToTerminateNoImprovement: opts.TimeToTerminateIfNoImprovement,
                                                    numThreads: opts.NumGurobiThreads,
                                                    recordProgress: storeProgress,
                                                    logPath: opts.LogFile),
                                topology, clusters);
                    }
                    break;
                default:
                    throw new Exception("Other solvers are currently invalid.");
            }
        }

        // TODO: this function is missing proper commenting
        private static void RunNetwork<TVar, TSolution>(ISolver<TVar, TSolution> solver,
                Topology topology, List<Topology> clusters)
        {
            var opts = CliOptions.Instance;

            // setup the optimal encoder and adversarial input generator.
            var optimalEncoder = new TEMaxFlowOptimalEncoder<TVar, TSolution>(solver, opts.Paths);
            TEAdversarialInputGenerator<TVar, TSolution> adversarialInputGenerator;
            adversarialInputGenerator = new TEAdversarialInputGenerator<TVar, TSolution>(topology, opts.Paths, opts.NumProcesses);

            // setup the heuristic encoder and partitions.
            var heuristicSolver = solver;
            var (heuristicEncoder, partitioning, partitionList) = CliUtils.getHeuristic<TVar, TSolution>(heuristicSolver, topology, opts.Heuristic, opts.Paths, opts.PopSlices,
                        opts.DemandPinningThreshold * opts.DownScaleFactor, numSamples: opts.NumRandom, partitionSensitivity: opts.PartitionSensitivity,
                        scaleFactor: opts.DownScaleFactor, InnerEncoding: opts.InnerEncoding, maxShortestPathLen: opts.MaxShortestPathLen);

            // find an adversarial example and show the time taken.
            var demandList = new GenericList((opts.DemandList.Split(",")).Select(x => double.Parse(x) * opts.DownScaleFactor).ToHashSet());
            Utils.logger(
                string.Format("Demand List:{0}", Newtonsoft.Json.JsonConvert.SerializeObject(demandList.List, Newtonsoft.Json.Formatting.Indented)),
                opts.Verbose);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Utils.logger("Starting setup", opts.Verbose);
            (TEOptimizationSolution, TEOptimizationSolution) result;
            switch (opts.Method)
            {
                case MethodChoice.Direct:
                    result = CliUtils.getMetaOptResult(adversarialInputGenerator, optimalEncoder, heuristicEncoder, opts.DemandUB, opts.InnerEncoding,
                                        demandList, opts.EnableClustering, opts.ClusterVersion, clusters, opts.NumInterClusterSamples, opts.NumNodesPerCluster,
                                        opts.NumInterClusterQuantizations, opts.Simplify, opts.Verbose, opts.MaxDensity, opts.LargeDemandLB, opts.maxLargeDistance,
                                        opts.maxSmallDistance, false, null);
                    break;
                case MethodChoice.Search:
                    Utils.logger("Going to use search to find a desirable gap", opts.Verbose);
                    result = adversarialInputGenerator.FindMaximumGapInterval(optimalEncoder, heuristicEncoder, opts.Confidencelvl, opts.StartingGap, opts.DemandUB,
                            demandList: demandList);
                    break;
                case MethodChoice.FindFeas:
                    Utils.logger("Going to find one feasible solution with the specified gap", opts.Verbose);
                    result = adversarialInputGenerator.FindOptimalityGapAtLeast(optimalEncoder, heuristicEncoder, opts.StartingGap, opts.DemandUB,
                            demandList: demandList, simplify: opts.Simplify);
                    break;
                case MethodChoice.Random:
                    Utils.logger("Going to do random search to find some advers inputs", opts.Verbose);
                    result = adversarialInputGenerator.RandomAdversarialGenerator(optimalEncoder, heuristicEncoder, opts.NumRandom, opts.DemandUB, seed: opts.Seed,
                        verbose: opts.Verbose, storeProgress: opts.StoreProgress, logPath: opts.LogFile, timeout: opts.Timeout);
                    break;
                case MethodChoice.HillClimber:
                    Utils.logger("Going to use HillClimber to find some advers inputs", opts.Verbose);
                    result = adversarialInputGenerator.HillClimbingAdversarialGenerator(optimalEncoder, heuristicEncoder, opts.NumRandom,
                        opts.NumNeighbors, opts.DemandUB, opts.StdDev, seed: opts.Seed, verbose: opts.Verbose, storeProgress: opts.StoreProgress,
                        logPath: opts.LogFile, timeout: opts.Timeout);
                    break;
                case MethodChoice.SimulatedAnnealing:
                    Utils.logger("Going to use Simulated Annealing to find some advers inputs", opts.Verbose);
                    Utils.logger(opts.LogFile, opts.Verbose);
                    result = adversarialInputGenerator.SimulatedAnnealing(optimalEncoder, heuristicEncoder, opts.NumRandom, opts.NumNeighbors,
                        opts.DemandUB, opts.StdDev, opts.InitTmp, opts.TmpDecreaseFactor, seed: opts.Seed, verbose: opts.Verbose, storeProgress: opts.StoreProgress,
                        logPath: opts.LogFile, timeout: opts.Timeout);
                    break;
                default:
                    throw new Exception("Wrong Method, please choose between available methods!!");
            }

            if (opts.FullOpt)
            {
                if (!opts.EnableClustering)
                {
                    throw new Exception("does not need to be enable for non-clustering method");
                }
                if (opts.InnerEncoding != InnerRewriteMethodChoice.PrimalDual)
                {
                    throw new Exception("inner encoding should be primal dual");
                }
                optimalEncoder.Solver.CleanAll(timeout: opts.FullOptTimer);
                var currDemands = new Dictionary<(string, string), double>(result.Item1.Demands);
                Utils.setEmptyPairsToZero(topology, currDemands);
                result = adversarialInputGenerator.MaximizeOptimalityGap(optimalEncoder, heuristicEncoder, opts.DemandUB, innerEncoding: opts.InnerEncoding,
                        demandList: demandList, simplify: opts.Simplify, verbose: opts.Verbose, demandInits: currDemands);
                optimalEncoder.Solver.CleanAll(focusBstBd: false, timeout: opts.Timeout);
            }

            if (opts.UBFocus)
            {
                var currDemands = new Dictionary<(string, string), double>(result.Item1.Demands);
                optimalEncoder.Solver.CleanAll(focusBstBd: true, timeout: opts.UBFocusTimer);
                Utils.setEmptyPairsToZero(topology, currDemands);
                result = adversarialInputGenerator.MaximizeOptimalityGap(optimalEncoder, heuristicEncoder, opts.DemandUB, innerEncoding: opts.InnerEncoding,
                        demandList: demandList, simplify: opts.Simplify, verbose: opts.Verbose, demandInits: currDemands);
                optimalEncoder.Solver.CleanAll(focusBstBd: false, timeout: opts.Timeout);
            }
            var optimal = result.Item1.MaxObjective;
            var heuristic = result.Item2.MaxObjective;
            var demands = new Dictionary<(string, string), double>(result.Item1.Demands);
            Utils.setEmptyPairsToZero(topology, demands);
            Console.WriteLine("##############################################");
            Console.WriteLine("##############################################");
            Console.WriteLine("##############################################");
            Console.WriteLine($"optimal={optimal}, heuristic={heuristic}, time={timer.ElapsedMilliseconds}ms");
            if (opts.Heuristic == Heuristic.ExpectedPop)
            {
                CliUtils.findGapExpectedPopAdversarialDemandOnIndependentPartitions<GRBVar, GRBModel>(opts, topology, demands, optimal);
            }
            Console.WriteLine("##############################################");
            Console.WriteLine("##############################################");
            Console.WriteLine("##############################################");
            var optGSolver = new GurobiBinary();
            var optimalEncoderG = new TEMaxFlowOptimalEncoder<GRBVar, GRBModel>(optGSolver, maxNumPaths: opts.Paths);
            var optZSolver = new SolverZen();
            var optimalEncoderZen = new TEMaxFlowOptimalEncoder<Zen<Real>, ZenSolution>(optZSolver, maxNumPaths: opts.Paths);

            var gSolver = new GurobiBinary();
            var zSolver = new SolverZen();
            IEncoder<GRBVar, GRBModel> heuristicEncoderG;
            IEncoder<Zen<Real>, ZenSolution> heuristicEncoderZ;
            switch (opts.Heuristic)
            {
                case Heuristic.Pop:
                    Console.WriteLine("Starting exploring pop heuristic");
                    heuristicEncoderG = new PopEncoder<GRBVar, GRBModel>(gSolver, maxNumPaths: opts.Paths, numPartitions: opts.PopSlices, demandPartitions: partitioning);
                    heuristicEncoderZ = new PopEncoder<Zen<Real>, ZenSolution>(zSolver, maxNumPaths: opts.Paths, numPartitions: opts.PopSlices, demandPartitions: partitioning);
                    break;
                case Heuristic.DemandPinning:
                    Console.WriteLine("Starting exploring demand pinning heuristic");
                    heuristicEncoderG = new DirectDemandPinningEncoder<GRBVar, GRBModel>(gSolver, k: opts.Paths, threshold: opts.DemandPinningThreshold * opts.DownScaleFactor);
                    heuristicEncoderZ = new DirectDemandPinningEncoder<Zen<Real>, ZenSolution>(zSolver, k: opts.Paths, threshold: opts.DemandPinningThreshold * opts.DownScaleFactor);
                    break;
                case Heuristic.ExpectedPop:
                    Console.WriteLine("Starting to explore expected pop heuristic");
                    heuristicEncoderG = new ExpectedPopEncoder<GRBVar, GRBModel>(gSolver, k: opts.Paths, numSamples: opts.NumRandom,
                        numPartitionsPerSample: opts.PopSlices, demandPartitionsList: partitionList);
                    heuristicEncoderZ = new ExpectedPopEncoder<Zen<Real>, ZenSolution>(zSolver, k: opts.Paths, numSamples: opts.NumRandom,
                        numPartitionsPerSample: opts.PopSlices, demandPartitionsList: partitionList);
                    break;
                case Heuristic.PopDp:
                    throw new Exception("Not Implemented Yet.");
                default:
                    throw new Exception("No heuristic selected.");
            }
            Utils.checkSolution(topology, heuristicEncoderG, optimalEncoderG, heuristic, optimal, demands, "gurobiCheck");
        }
    }
}
