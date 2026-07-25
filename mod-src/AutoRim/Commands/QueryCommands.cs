using System;
using System.Collections.Generic;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using AutoRim.Read;
using RimWorld;
using Verse;

namespace AutoRim.Commands
{
    /// <summary>
    /// Def lookup. Most command failures come from naming something the game does not call
    /// what a person calls it, so this is the tool to reach for before guessing again.
    /// </summary>
    public class SearchDefsCommand : CommandBase
    {
        public override string Name => "query.search_defs";
        public override string Description =>
            "Searches definitions by name. type: thing, terrain, recipe, research, work, skill, trait, hediff, pawnkind, plant, weapon, apparel, building.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            string type = args.RequireString("type").ToLowerInvariant();
            string query = args.OptString("query", "");
            int limit = Math.Min(Math.Max(args.OptInt("limit", 20), 1), 100);

            List<Def> results = Search(type, query, limit);

            var array = JsonValue.NewArray();
            foreach (var def in results)
            {
                var entry = JsonValue.NewObject()
                    .Set("defName", def.defName)
                    .Set("label", def.label ?? "");

                if (def is ThingDef thing)
                {
                    entry.Set("category", thing.category.ToString());
                    if (thing.MadeFromStuff) entry.Set("madeFromStuff", true);
                }

                array.Add(entry);
            }

            return JsonValue.NewObject()
                .Set("type", type)
                .Set("query", query)
                .Set("count", array.Count)
                .Set("results", array);
        }

        private static List<Def> Search(string type, string query, int limit)
        {
            switch (type)
            {
                case "thing": return DefResolver.Search<ThingDef>(query, limit).Cast<Def>().ToList();
                case "terrain": return DefResolver.Search<TerrainDef>(query, limit).Cast<Def>().ToList();
                case "recipe": return DefResolver.Search<RecipeDef>(query, limit).Cast<Def>().ToList();
                case "research": return DefResolver.Search<ResearchProjectDef>(query, limit).Cast<Def>().ToList();
                case "work": return DefResolver.Search<WorkTypeDef>(query, limit).Cast<Def>().ToList();
                case "skill": return DefResolver.Search<SkillDef>(query, limit).Cast<Def>().ToList();
                case "trait": return DefResolver.Search<TraitDef>(query, limit).Cast<Def>().ToList();
                case "hediff": return DefResolver.Search<HediffDef>(query, limit).Cast<Def>().ToList();
                case "pawnkind": return DefResolver.Search<PawnKindDef>(query, limit).Cast<Def>().ToList();
                case "biome": return DefResolver.Search<BiomeDef>(query, limit).Cast<Def>().ToList();

                // Convenience filters over ThingDef, which is what callers usually mean.
                case "plant":
                    return Filtered(query, limit, d => d.plant != null);
                case "weapon":
                    return Filtered(query, limit, d => d.IsWeapon);
                case "apparel":
                    return Filtered(query, limit, d => d.IsApparel);
                case "building":
                    return Filtered(query, limit, d => d.category == ThingCategory.Building);

                default:
                    throw CommandException.BadArgs($"Unknown def type '{type}'.",
                        "Use thing, terrain, recipe, research, work, skill, trait, hediff, pawnkind, biome, plant, weapon, apparel or building.");
            }
        }

        private static List<Def> Filtered(string query, int limit, Func<ThingDef, bool> predicate)
        {
            return DefResolver.Search<ThingDef>(query, limit * 6)
                .Where(predicate)
                .Take(limit)
                .Cast<Def>()
                .ToList();
        }
    }

    public class ThingInfoCommand : CommandBase
    {
        public override string Name => "query.thing_info";
        public override string Description =>
            "Details for one thing definition: build cost, stuff, research needed, size, and how it is made.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            var def = DefResolver.Resolve<ThingDef>(args.RequireString("thing"), "thing");

            var result = JsonValue.NewObject()
                .Set("defName", def.defName)
                .Set("label", def.label ?? "")
                .Set("description", def.description ?? "")
                .Set("category", def.category.ToString())
                .Set("techLevel", def.techLevel.ToString());

            if (def.stackLimit > 1) result.Set("stackLimit", def.stackLimit);
            if (def.category == ThingCategory.Building)
                result.Set("size", JsonValue.NewObject().Set("x", def.Size.x).Set("z", def.Size.z));

            if (def.MadeFromStuff && def.stuffCategories != null)
            {
                result.Set("madeFromStuff", true);
                result.Set("stuffCategories", Describe.ToArray(def.stuffCategories.Select(c => c.defName)));
                result.Set("stuffCost", def.costStuffCount);
            }

            if (def.costList != null && def.costList.Count > 0)
            {
                var costs = JsonValue.NewArray();
                foreach (var cost in def.costList)
                    costs.Add(JsonValue.NewObject()
                        .Set("defName", cost.thingDef?.defName ?? "")
                        .Set("label", cost.thingDef?.label ?? "")
                        .Set("count", cost.count));
                result.Set("costList", costs);
            }

            if (def.researchPrerequisites != null && def.researchPrerequisites.Count > 0)
            {
                var research = JsonValue.NewArray();
                foreach (var project in def.researchPrerequisites)
                    research.Add(JsonValue.NewObject()
                        .Set("defName", project.defName)
                        .Set("label", project.label)
                        .Set("finished", project.IsFinished));
                result.Set("researchPrerequisites", research);
            }

            if (def.plant != null)
            {
                var plant = JsonValue.NewObject()
                    .Set("growDays", Describe.Round(def.plant.growDays))
                    .Set("sowMinSkill", def.plant.sowMinSkill)
                    .Set("harvestYield", Describe.Round(def.plant.harvestYield));
                if (def.plant.harvestedThingDef != null)
                    plant.Set("harvestedThing", def.plant.harvestedThingDef.label);
                result.Set("plant", plant);
            }

            var madeBy = DefDatabase<RecipeDef>.AllDefsListForReading
                .Where(r => r.products != null && r.products.Any(p => p.thingDef == def))
                .Take(10)
                .ToList();

            if (madeBy.Count > 0)
                result.Set("madeByRecipes", DefResolver.Describe(madeBy));

            return result;
        }
    }

    public class RecipeInfoCommand : CommandBase
    {
        public override string Name => "query.recipe_info";
        public override string Description =>
            "Details for a recipe: ingredients, products, work amount, skill needed, and which benches can run it.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            var recipe = DefResolver.Resolve<RecipeDef>(args.RequireString("recipe"), "recipe");

            var result = JsonValue.NewObject()
                .Set("defName", recipe.defName)
                .Set("label", recipe.label ?? "")
                .Set("description", recipe.description ?? "")
                .Set("workAmount", Describe.Round(recipe.workAmount));

            if (recipe.workSkill != null) result.Set("workSkill", recipe.workSkill.label);

            if (recipe.skillRequirements != null && recipe.skillRequirements.Count > 0)
            {
                var requirements = JsonValue.NewArray();
                foreach (var requirement in recipe.skillRequirements)
                    requirements.Add(JsonValue.NewObject()
                        .Set("skill", requirement.skill?.label ?? "")
                        .Set("minLevel", requirement.minLevel));
                result.Set("skillRequirements", requirements);
            }

            if (recipe.ingredients != null && recipe.ingredients.Count > 0)
            {
                var ingredients = JsonValue.NewArray();
                foreach (var ingredient in recipe.ingredients)
                    ingredients.Add(JsonValue.NewObject()
                        .Set("summary", ingredient.Summary)
                        .Set("count", Describe.Round(ingredient.GetBaseCount())));
                result.Set("ingredients", ingredients);
            }

            if (recipe.products != null && recipe.products.Count > 0)
            {
                var products = JsonValue.NewArray();
                foreach (var product in recipe.products)
                    products.Add(JsonValue.NewObject()
                        .Set("defName", product.thingDef?.defName ?? "")
                        .Set("label", product.thingDef?.label ?? "")
                        .Set("count", product.count));
                result.Set("products", products);
            }

            if (recipe.researchPrerequisite != null)
                result.Set("research", JsonValue.NewObject()
                    .Set("defName", recipe.researchPrerequisite.defName)
                    .Set("label", recipe.researchPrerequisite.label)
                    .Set("finished", recipe.researchPrerequisite.IsFinished));

            try
            {
                var users = recipe.AllRecipeUsers?.Take(10).ToList();
                if (users != null && users.Count > 0)
                    result.Set("workbenches", DefResolver.Describe(users));
            }
            catch (Exception)
            {
                // AllRecipeUsers walks every ThingDef and can throw on malformed modded defs.
            }

            return result;
        }
    }
}
