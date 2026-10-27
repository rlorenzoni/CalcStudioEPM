using System.Collections.Generic;
using System.Linq;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// What one deploy sends where: always Calculation Manager, and Planning (HP) too when
    /// it was asked for and every %Template call could be inlined. Built once by
    /// <see cref="Create"/> and shared by Deploy and Execute's "deploy my changes first".
    /// </summary>
    public sealed class DeployPlan
    {
        /// <summary>Editor content — the Calculation Manager script.</summary>
        public string Content { get; private set; }

        /// <summary>A graphical rule: its whole CALC rule XML, rebuilt from the rule's folder
        /// (see <see cref="GraphicalRule"/>). Null for a script rule.</summary>
        public string RuleXml { get; private set; }

        /// <summary>The method asked for (toolbar drop-down or connection default).</summary>
        public DeployMethod Requested { get; private set; }

        /// <summary>Script for the Planning copy (templates inlined); null = CM only.</summary>
        public string PlanningScript { get; private set; }

        /// <summary>Templates inlined into <see cref="PlanningScript"/>.</summary>
        public IReadOnlyList<string> InlinedTemplates { get; private set; } = new string[0];

        /// <summary>Why Planning was asked for but can't be deployed (complex templates).</summary>
        public IReadOnlyList<string> PlanningBlockers { get; private set; } = new string[0];

        public bool IncludesPlanning => PlanningScript != null;

        /// <summary>CM+HP was asked for but only CM is possible: the user must deploy the rule
        /// to Planning from Calculation Manager.</summary>
        public bool ActionNeeded => Requested == DeployMethod.CmAndHp && !IncludesPlanning;

        public string TargetsText => IncludesPlanning
            ? "Calculation Manager and Planning"
            : "Calculation Manager only";

        /// <summary>A graphical rule goes to Calculation Manager only (deployed to Planning from there).</summary>
        internal static DeployPlan Graphical(string ruleXml) =>
            new DeployPlan { Content = "(graphical rule)", RuleXml = ruleXml, Requested = DeployMethod.CmOnly };

        /// <summary>A RuleSet: its whole XML, put together by <see cref="Epm.RuleSet.Assemble"/>.</summary>
        public bool IsRuleSet { get; private set; }

        /// <summary>A RuleSet goes to Calculation Manager only (deployed to Planning from there).</summary>
        internal static DeployPlan ForRuleSet(string ruleSetXml) =>
            new DeployPlan { Content = "(RuleSet)", RuleXml = ruleSetXml, Requested = DeployMethod.CmOnly, IsRuleSet = true };

        internal static DeployPlan Create(string localFolder, string content, DeployMethod method)
        {
            var plan = new DeployPlan { Content = content, Requested = method };
            if (method != DeployMethod.CmAndHp) return plan;

            TemplateExpansion exp = TemplateExpander.Expand(localFolder, PackageBuilder.ResolveScriptText(content));
            if (exp.CanDeployToPlanning)
            {
                plan.PlanningScript   = exp.Script;
                plan.InlinedTemplates = exp.Inlined.ToList();
            }
            else
            {
                // One line per template and reason, with the lines it is called from
                plan.PlanningBlockers = exp.Issues
                    .GroupBy(i => (i.TemplateName, i.Reason))
                    .Select(g =>
                    {
                        var lines = g.Where(i => i.Line > 0).Select(i => i.Line).Distinct().ToList();
                        string where = lines.Count == 0 ? "" :
                            (lines.Count == 1 ? " (line " : " (lines ") + string.Join(", ", lines) + ")";
                        return $"'{g.Key.TemplateName}'{where} — {g.Key.Reason}";
                    })
                    .ToList();
            }
            return plan;
        }
    }
}
