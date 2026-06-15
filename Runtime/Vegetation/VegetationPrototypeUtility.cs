using System.Collections.Generic;

namespace ZGConnect
{
    public static class VegetationPrototypeUtility
    {
        /// <summary>
        /// Unique prototypes referenced by all species sets in the rule set (stable order).
        /// </summary>
        public static VegetationPrototype[] CollectFromRuleSet(VegetationRuleSet ruleSet)
        {
            if (ruleSet?.rules == null || ruleSet.rules.Length == 0)
                return System.Array.Empty<VegetationPrototype>();

            var seen   = new HashSet<VegetationPrototype>();
            var result = new List<VegetationPrototype>();

            foreach (VegetationRule rule in ruleSet.rules)
            {
                if (rule?.speciesSet?.prototypes == null)
                    continue;

                foreach (VegetationPrototype proto in rule.speciesSet.prototypes)
                {
                    if (proto == null || !seen.Add(proto))
                        continue;
                    result.Add(proto);
                }
            }

            return result.ToArray();
        }
    }
}

