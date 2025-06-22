using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

class XmlNamingValidator
{
    public static void ValidatePluralParents(XDocument xdoc)
    {
        ValidateElement(xdoc.Root);
    }

    static void ValidateElement(XElement element)
    {
        // Group direct children by tag name
        var groups = element.Elements().GroupBy(e => e.Name.LocalName);
        foreach (var grp in groups)
        {
            var childName = grp.Key;
            int count = grp.Count();
            if (count > 1)
            {
                // Expect parent name to be a plural of childName
                string parentName = element.Name.LocalName;
                if (!IsPluralOf(parentName, childName))
                {
                    Console.WriteLine(
                        $"Warning: Element <{parentName}> has {count} <{childName}> children, " +
                        $"but parent name is not plural of child (expected e.g. \"{childName}s\" or configured plural).");
                }
            }
        }
        // Recurse
        foreach (var child in element.Elements())
            ValidateElement(child);
    }

    // Naive plural check: parentName equals childName + "s" or childName + "es".
    // You can extend this with a more complete pluralization dictionary if needed.
    static bool IsPluralOf(string parentName, string childName)
    {
        if (string.Equals(parentName, childName + "s", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(parentName, childName + "es", StringComparison.OrdinalIgnoreCase))
            return true;
        // Add any irregulars manually:
        // e.g. if childName=="Category" and parentName=="Categories"
        if (childName.EndsWith("y", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parentName,
                             childName.Substring(0, childName.Length - 1) + "ies",
                             StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
