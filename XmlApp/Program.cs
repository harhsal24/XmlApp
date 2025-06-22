using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

class Program
{
    static void Main(string[] args)
    {
        string projectRoot = Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(projectRoot, "xmls")))
        {
            var exeDir = AppContext.BaseDirectory;
            var dir = new DirectoryInfo(exeDir);
            for (int i = 0; i < 5; i++)
            {
                dir = dir.Parent;
                if (dir == null) break;
                if (Directory.Exists(Path.Combine(dir.FullName, "xmls")))
                {
                    projectRoot = dir.FullName;
                    break;
                }
            }
        }

        var xmlDir = Path.Combine(projectRoot, "xmls");
        var mappingDir = Path.Combine(projectRoot, "mapping");
        var outputDir = Path.Combine(projectRoot, "output");
        Directory.CreateDirectory(outputDir);

        bool doExtract = false, doReorder = false, doFindDuplicates = false;

        if (args.Length > 0)
        {
            foreach (var arg in args)
            {
                if (arg.Equals("extract", StringComparison.OrdinalIgnoreCase))
                    doExtract = true;
                if (arg.Equals("reorder", StringComparison.OrdinalIgnoreCase))
                    doReorder = true;
                if (arg.Equals("duplicates", StringComparison.OrdinalIgnoreCase))
                    doFindDuplicates = true;
            }
        }
        else
        {
            Console.WriteLine("Choose operation:");
            Console.WriteLine("1. extract      (generate ordered leaf-path files from xmls/)");
            Console.WriteLine("2. reorder      (reorder mapping files in mapping/ using ordered-xpaths.txt)");
            Console.WriteLine("3. both extract & reorder");
            Console.WriteLine("4. find-duplicates  (find duplicate mappings in mapping/)");
            Console.Write("Enter 1, 2, 3, or 4: ");
            var choice = Console.ReadLine();
            if (choice == "1") doExtract = true;
            else if (choice == "2") doReorder = true;
            else if (choice == "3") { doExtract = doReorder = true; }
            else if (choice == "4") doFindDuplicates = true;
            else
            {
                Console.WriteLine("Invalid choice. Exiting.");
                return;
            }
        }

        if (doExtract)
            ExtractLeafPaths(xmlDir, outputDir);

        if (doReorder)
            ReorderMappings(mappingDir, xmlDir, outputDir);

        if (doFindDuplicates)
            FindDuplicateMappings(mappingDir, outputDir);

        Console.WriteLine("Done.");
    }

    static void ExtractLeafPaths(string xmlDir, string outputDir)
    {
        if (!Directory.Exists(xmlDir))
        {
            Console.WriteLine($"[Extract] xmls directory not found: {xmlDir}");
            return;
        }

        var xmlFiles = Directory.GetFiles(xmlDir, "*.xml");
        if (xmlFiles.Length == 0)
        {
            Console.WriteLine($"[Extract] No .xml files found in {xmlDir}");
            return;
        }

        foreach (var file in xmlFiles)
        {
            Console.WriteLine($"[Extract] Processing {Path.GetFileName(file)}...");
            XDocument xdoc;
            try
            {
                string xmlText = File.ReadAllText(file);
                var matches = Regex.Matches(xmlText, @"xmlns:xlink=""[^""]+""");
                if (matches.Count > 1)
                {
                    for (int i = 1; i < matches.Count; i++)
                        xmlText = xmlText.Replace(matches[i].Value, "");
                }
                xdoc = XDocument.Parse(xmlText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed to load XML: {ex.Message}");
                continue;
            }

            var paths = new List<string>();
            Traverse(xdoc.Root, "", paths);

            var extended = new List<string>();
            foreach (var p in paths)
            {
                if (p.Contains("[1]"))
                {
                    var noIndex = Regex.Replace(p, @"\[\s*1\s*\]", "");
                    extended.Add(noIndex);
                    extended.Add(p);
                }
                else
                {
                    extended.Add(p);
                }
            }

            var outPath = Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(file) + "_ordered_leaf_paths.txt");
            using (var writer = new StreamWriter(outPath))
            {
                for (int i = 0; i < extended.Count; i++)
                    writer.WriteLine($"{i + 1}. {extended[i]}");
            }
            Console.WriteLine($"  Extracted {extended.Count} paths → {Path.GetFileName(outPath)}");
        }
    }

    static void ReorderMappings(string mappingDir, string xmlDir, string outputDir)
    {
        var orderedPaths = File.ReadAllLines(Path.Combine(mappingDir, "ordered-xpaths.txt"))
            .Select(line =>
            {
                var t = line.Trim();
                var idx = t.IndexOf(' ');
                return (idx > 0 && int.TryParse(t.Substring(0, idx).TrimEnd('.'), out _))
                    ? t.Substring(idx + 1).Trim()
                    : t;
            })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        var srcFile = Directory.GetFiles(xmlDir, "*.xml").First();
        var srcDoc = XDocument.Load(srcFile);
        XNamespace xl = "http://www.w3.org/1999/xlink";

        var pathToLabel = new Dictionary<string, string>();
        foreach (var raw in orderedPaths)
        {
            try
            {
                var elem = srcDoc.XPathSelectElement(raw);
                if (elem == null)
                {
                    pathToLabel[raw] = null;
                    continue;
                }
                var prop = elem.Ancestors("PROPERTY").FirstOrDefault();
                pathToLabel[raw] = prop?.Attribute(xl + "label")?.Value ?? "SubjectProperty";
            }
            catch
            {
                pathToLabel[raw] = null;
            }
        }

        var labelsInOrder = orderedPaths
            .Select(p => pathToLabel[p])
            .Where(l => l != null)
            .Distinct()
            .ToList();

        foreach (var mapFile in Directory.GetFiles(mappingDir, "*.xml"))
        {
            Console.WriteLine($"[Reorder] {Path.GetFileName(mapFile)}");
            var mapDoc = XDocument.Load(mapFile);
            var commons = mapDoc.Root.Elements("common").ToList();

            var lookup = new Dictionary<string, Queue<XElement>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in commons)
            {
                var xp = c.Element("UAD_Xpath")?.Value.Trim();
                if (xp == null) continue;
                var norm = NormalizePath(xp);
                if (!lookup.ContainsKey(norm))
                    lookup[norm] = new Queue<XElement>();
                lookup[norm].Enqueue(c);
            }

            var newRoot = new XElement("mappings");

            foreach (var label in labelsInOrder)
            {
                newRoot.Add(new XComment($" === {label} start === "));
                var myPaths = orderedPaths.Where(p => pathToLabel[p] == label);
                bool any = false;

                foreach (var raw in myPaths)
                {
                    var norm = NormalizePath(raw);
                    if (lookup.TryGetValue(norm, out var q) && q.Count > 0)
                    {
                        while (q.Count > 0)
                        {
                            newRoot.Add(q.Dequeue());
                            any = true;
                        }
                    }
                }

                if (!any)
                    newRoot.Add(new XComment($" (no mappings found for {label}) "));
                newRoot.Add(new XComment($" === {label} end === "));
            }

            var leftovers = lookup.Values.SelectMany(q => q).ToList();
            if (leftovers.Any())
            {
                newRoot.Add(new XComment(" === Unmatched mappings === "));
                foreach (var c in leftovers)
                    newRoot.Add(c);
                newRoot.Add(new XComment(" === End of Unmatched mappings === "));
            }

            var outFile = Path.Combine(outputDir, "reordered-" + Path.GetFileName(mapFile));
            new XDocument(newRoot).Save(outFile);
            Console.WriteLine($"  → {Path.GetFileName(outFile)}");
        }
    }

    static void Traverse(XElement element, string currentPath, List<string> result)
    {
        string name = element.Name.LocalName;
        bool isProp = name == "PROPERTY" && element.Attribute("ValuationUseType") != null;
        string pred = isProp ? $"[@ValuationUseType='{element.Attribute("ValuationUseType").Value}']" : "";
        string segment = name + pred;

        string idxSuffix = "";
        var parent = element.Parent;
        if (parent != null)
        {
            Func<XElement, string> key = e =>
                e.Name.LocalName == "PROPERTY" && e.Attribute("ValuationUseType") != null
                    ? e.Name.LocalName + "|" + e.Attribute("ValuationUseType").Value
                    : e.Name.LocalName;

            var siblings = parent.Elements().Where(e => key(e) == key(element)).ToList();
            if (siblings.Count > 1)
            {
                int pos = siblings.IndexOf(element) + 1;
                idxSuffix = $"[{pos}]";
            }
        }

        var pathHere = string.IsNullOrEmpty(currentPath)
            ? "/" + segment + idxSuffix
            : currentPath + "/" + segment + idxSuffix;

        bool hasChild = element.Elements().Any();
        bool hasText = !string.IsNullOrWhiteSpace(element.Value);
        if (!hasChild && hasText)
        {
            result.Add(pathHere);
            return;
        }

        foreach (var child in element.Elements())
            Traverse(child, pathHere, result);
    }

    static string NormalizePath(string path)
        => Regex.Replace(path, @"\[\s*\d+\s*\]", "").Trim();

    static void FindDuplicateMappings(string mappingDir, string outputDir)
    {
        Console.WriteLine("[Duplicates] Scanning for duplicate mappings...");

        foreach (var mapFile in Directory.GetFiles(mappingDir, "*.xml"))
        {
            var doc = XDocument.Load(mapFile);
            var commons = doc.Root.Elements("common").ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<XElement>();

            foreach (var c in commons)
            {
                var xpath = c.Element("UAD_Xpath")?.Value?.Trim();
                var tag = c.Element("ACI_Tag")?.Value?.Trim();

                if (string.IsNullOrEmpty(xpath) || string.IsNullOrEmpty(tag))
                    continue;

                string key = NormalizePath(xpath) + "|" + tag.ToLowerInvariant().Trim();

                if (!seen.Add(key))
                {
                    duplicates.Add(new XElement(c));
                }
            }

            if (duplicates.Any())
            {
                string outFile = Path.Combine(outputDir, "duplicates-" + Path.GetFileName(mapFile));
                new XDocument(new XElement("duplicates", duplicates)).Save(outFile);
                Console.WriteLine($"  Found {duplicates.Count} duplicates → {Path.GetFileName(outFile)}");
            }
            else
            {
                Console.WriteLine($"  No duplicates found in {Path.GetFileName(mapFile)}");
            }
        }
    }
}
