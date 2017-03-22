﻿using Medallion.Collections;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Playground.BalancingTokenParse
{
    internal class ParserBuilder
    {
        private readonly FirstFollowCalculator baseFirstFollow;
        private readonly InternalFirstFollowProvider firstFollow;
        private readonly Dictionary<NonTerminal, IReadOnlyList<Rule>> rules;
        private readonly Queue<NonTerminal> remainingSymbols;
        private readonly Dictionary<NonTerminal, List<DiscriminatorPrefixInfo>> discriminatorSymbols = new Dictionary<NonTerminal, List<DiscriminatorPrefixInfo>>();
        private readonly Dictionary<IReadOnlyCollection<PartialRule>, IParserNode> cache =
            new Dictionary<IReadOnlyCollection<PartialRule>, IParserNode>(EqualityComparers.GetCollectionComparer<PartialRule>());

        private ParserBuilder(IEnumerable<Rule> rules)
        {
            this.rules = rules.GroupBy(r => r.Produced)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<Rule>)g.ToArray());
            this.baseFirstFollow = new FirstFollowCalculator(this.rules.SelectMany(kvp => kvp.Value).ToArray());
            this.firstFollow = new InternalFirstFollowProvider(this.baseFirstFollow);
            this.remainingSymbols = new Queue<NonTerminal>(this.baseFirstFollow.NonTerminals);
        }

        public static Dictionary<NonTerminal, IParserNode> CreateParser(IEnumerable<Rule> rules)
        {
            return new ParserBuilder(rules).CreateParser();
        }

        private Dictionary<NonTerminal, IParserNode> CreateParser()
        {
            var result = new Dictionary<NonTerminal, IParserNode>();
            while (this.remainingSymbols.Count > 0)
            {
                var next = this.remainingSymbols.Dequeue();
                result.Add(next, this.CreateParserNode(next));
            }

            return result;
        }

        private IParserNode CreateParserNode(NonTerminal symbol)
        {
            return this.CreateParserNode(this.rules[symbol].Select(r => new PartialRule(r)).ToArray());
        }

        private IParserNode CreateParserNode(IReadOnlyList<PartialRule> rules)
        {
            // it's important to cache at this level rather than at Create(NonTerminal) because
            // this encompasses all of the former's cache hits but will get other cache hits as well

            IParserNode existing;
            if (this.cache.TryGetValue(rules, out existing))
            {
                return existing;
            }

            var created = this.CreateParserNodeNoCache(rules);
            // so far I haven't hit a case where in computing a node I end up needing that node again. If that case
            // came up we might want to do something like put a "stub" node in the cache and return that, then fill it in
            // here to stop the recursion
            this.cache.Add(rules, created);
            return created;
        }

        private IParserNode CreateParserNodeNoCache(IReadOnlyList<PartialRule> rules)
        {
            // if we only have one rule, we just parse that
            if (rules.Count == 1)
            {
                return new ParseRuleNode(rules.Single());
            }

            // next, see what we can do with LL(1) single-token lookahead
            var tokenLookaheadTable = rules.SelectMany(r => this.firstFollow.NextOf(r), (r, t) => new { rule = r, token = t })
                .GroupBy(t => t.token, t => t.rule)
                .ToDictionary(g => g.Key, g => g.ToArray());

            // if there is only one entry in the table, just create a non-LL(1) node for that entry
            // we know that this must be non-LL(1) because we already checked for the single-rule case above
            if (tokenLookaheadTable.Count == 1)
            {
                return this.CreateNonLL1ParserNode(tokenLookaheadTable.Single().Key, tokenLookaheadTable.Single().Value);
            }

            // else, create a token lookahead node mapping from the table
            return new TokenLookaheadNode(
                tokenLookaheadTable.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Length == 1
                        ? new ParseRuleNode(kvp.Value.Single())
                        : this.CreateNonLL1ParserNode(kvp.Key, kvp.Value)
                )
            );
        }
        
        private IParserNode CreateNonLL1ParserNode(Token lookaheadToken, IReadOnlyList<PartialRule> rules)
        {
            // sanity checks
            if (rules.Count <= 1) { throw new ArgumentException(nameof(rules), "must be more than one"); }
            var produced = rules.Only(r => r.Produced);

            // look for a common prefix containing non-terminals (we reserve token-only prefixes for transformation into discriminators)

            var prefixLength = Enumerable.Range(0, count: rules.Min(r => r.Symbols.Count))
                .TakeWhile(i => rules.Skip(1).All(r => r.Symbols[i] == rules[0].Symbols[i]))
                .Select(i => i + 1)
                .LastOrDefault();

            if (prefixLength > 0 && !rules[0].Symbols.Take(prefixLength).All(r => r is Token))
            {
                return new ParsePrefixSymbolsNode(
                    rules[0].Symbols.Take(prefixLength),
                    this.CreateParserNode(rules.Select(r => new PartialRule(r, start: prefixLength)).ToArray())
                );
            }

            // next, if we are producing a discriminator, see if an existing discriminator is a prefix. This 
            // lets us handle recursion within the lookahead grammar
            if (!this.baseFirstFollow.NonTerminals.Contains(produced))
            {
                var match = this.discriminatorSymbols
                    // used to just check for equality, but this would find T0 as a match for T0'. Now we check for
                    // that as well. This is redundant with the final check but might help for speed
                    .Where(kvp => kvp.Key != produced && kvp.Value.All(i => i.Symbol != produced))
                    .Select(kvp => new { discriminator = kvp.Key, mapping = this.MapSymbolRules(kvp.Key, rules) })
                    .Where(t => t.mapping != null)
                    // we know all rules coming in have the given token in their next set. Only strip off a prefix where that token
                    // is part of the first set of all mapped rules, since otherwise we'll be unable to parse. In other words, don't
                    // consider a "prefix" which would be null given the lookahead
                    .Where(t => t.mapping.Values.All(r => this.firstFollow.FirstOf(r.Symbols).Contains(lookaheadToken)))
                    // don't consider a prefix mapping if it accounts for all symbols in the rules, since this can send us around
                    // in a loop when two discriminators have the same rules
                    .Where(t => t.mapping.Any(kvp => kvp.Key.Symbols.Count > kvp.Value.Symbols.Count))
                    .FirstOrDefault();
                if (match != null)
                {
                    // compute the effective follow of each mapped prefix rule based on the remaining suffix
                    var followMapping = match.mapping.GroupBy(kvp => kvp.Value, kvp => kvp.Key)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(pr => new PartialRule(pr, start: g.Key.Symbols.Count))
                                .Select(pr => this.firstFollow.NextOf(pr))
                                .Aggregate((s1, s2) => s1.Union(s2))
                        );
                    
                    Dictionary<PartialRule, Rule> mappingToUse;
                    IParserNode discriminatorParse;
                    DiscriminatorPrefixInfo existingPrefixInfo;
                    if (followMapping.All(kvp => kvp.Value.Except(this.firstFollow.FollowOf(kvp.Key)).Count == 0))
                    {
                        // if the discriminator follow set matches, just do a direct node parse on the discriminator. We
                        // know this is possible because all top-level discriminators must be parseable (since we might encounter
                        // GrammarLookahead nodes for them inside a lookahead)
                        mappingToUse = match.mapping;
                        discriminatorParse = this.CreateParserNode(match.discriminator);
                    }
                    else if ((existingPrefixInfo = this.discriminatorSymbols[match.discriminator]
                        .FirstOrDefault(
                            pi => followMapping.Select(
                                    kvp => new
                                    {
                                        rule = this.rules[pi.Symbol].Single(r => r.Symbols.SequenceEqual(kvp.Key.Symbols)),
                                        follow = kvp.Value
                                    }
                                )
                                .All(t => t.follow.Except(this.firstFollow.FollowOf(t.rule)).Count == 0)
                        )
                    ) != null)
                    {
                        // if we have an existing prefix info with a working follow set, use that
                        mappingToUse = match.mapping.ToDictionary(kvp => kvp.Key, kvp => this.rules[existingPrefixInfo.Symbol].Single(r => r.Symbols.SequenceEqual(kvp.Value.Symbols)));

                        IParserNode cachedNode;
                        if (existingPrefixInfo.NodeCache.TryGetValue(lookaheadToken, out cachedNode))
                        {
                            discriminatorParse = cachedNode;
                        }
                        else
                        {
                            discriminatorParse = this.CreateNonLL1ParserNode(lookaheadToken, mappingToUse.Values.Distinct().Select(r => new PartialRule(r)).ToArray());
                        }
                    }
                    else
                    {
                        // create and register the new sub-discriminator
                        var newSubDiscriminator = new NonTerminal(match.discriminator.Name + new string('\'', this.discriminatorSymbols[match.discriminator].Count + 1));
                        this.rules.Add(newSubDiscriminator, this.rules[match.discriminator].Select(r => new Rule(newSubDiscriminator, r.Symbols)).ToArray());
                        this.firstFollow.Register(this.rules[newSubDiscriminator].ToDictionary(
                            r => r,
                            r => followMapping.Single(kvp => kvp.Key.Symbols.SequenceEqual(r.Symbols)).Value
                        ));

                        var prefixInfo = new DiscriminatorPrefixInfo { Symbol = newSubDiscriminator };
                        this.discriminatorSymbols[match.discriminator].Add(prefixInfo);

                        mappingToUse = match.mapping.ToDictionary(kvp => kvp.Key, kvp => this.rules[newSubDiscriminator].Single(r => r.Symbols.SequenceEqual(kvp.Value.Symbols)));

                        // note that we go directly to a non-LL1 parse for the sub discriminator. This is correct because of where we are now. If the given
                        // set of rules isn't differentiable using LL(1) techniques then neither is the set of prefix rules! Furthermore, it's important that
                        // we compute a parse node ONLY for the current lookahead token, since sub discriminators might not be generally parseable under the full
                        // set of possible lookahead tokens
                        discriminatorParse = this.CreateNonLL1ParserNode(lookaheadToken, mappingToUse.Values.Distinct().Select(r => new PartialRule(r)).ToArray());
                        prefixInfo.NodeCache.Add(lookaheadToken, discriminatorParse);
                    }
                    
                    // map the discriminatorParse result to determine how to parse the remaining symbols
                    var mapResultNode = new MapResultNode(
                        discriminatorParse,
                        mappingToUse.GroupBy(kvp => kvp.Value, kvp => new PartialRule(kvp.Key, start: kvp.Value.Symbols.Count))
                            .ToDictionary(
                                g => g.Key,
                                g => this.CreateParserNode(g.ToArray())
                            )
                    );
                    return mapResultNode;
                }
            }

            // otherwise, we will need to create a new node as part of the lookahead grammar
            return this.CreateGrammarLookaheadParserNode(lookaheadToken, rules);
        }
        
        private IParserNode CreateGrammarLookaheadParserNode(Token lookaheadToken, IReadOnlyList<PartialRule> rules)
        {
            // sanity checks
            if (rules.Select(r => r.Rule).Distinct().Count() != rules.Count) { throw new ArgumentException(nameof(rules), "must be partials of distinct rules"); }

            var suffixToRuleMapping = rules.SelectMany(r => this.GatherPostTokenSuffixes(lookaheadToken, r), (r, suffix) => new { r, suffix })
                // note: this will throw if two rules have the same suffix, but it's not very elegant
                .ToDictionary(t => t.suffix, t => t.r, (IEqualityComparer<IReadOnlyList<Symbol>>)EqualityComparers.GetSequenceComparer<Symbol>());

            // create the discriminator symbol
            var discriminator = new NonTerminal("T" + this.discriminatorSymbols.Count);
            this.discriminatorSymbols.Add(discriminator, new List<DiscriminatorPrefixInfo>());
            var rulesAndFollowSets = suffixToRuleMapping.ToDictionary(kvp => new Rule(discriminator, kvp.Key), kvp => this.firstFollow.FollowOf(kvp.Value.Rule));
            this.rules.Add(discriminator, rulesAndFollowSets.Keys.ToArray());
            this.firstFollow.Register(rulesAndFollowSets);
            this.remainingSymbols.Enqueue(discriminator);
            
            return new GrammarLookaheadNode(
                lookaheadToken,
                discriminator,
                // map each discriminator rule back to the corresponding original rule
                this.rules[discriminator].ToDictionary(
                    r => r,
                    r => suffixToRuleMapping[r.Symbols].Rule
                )
            );
        }

        private Dictionary<PartialRule, Rule> MapSymbolRules(NonTerminal discriminator, IReadOnlyCollection<PartialRule> toMap)
        {
            var result = new Dictionary<PartialRule, Rule>();
            foreach (var rule in this.rules[discriminator])
            {
                if (rule.Symbols.Count == 0) { return null; }

                foreach (var match in toMap.Where(r => r.Symbols.Take(rule.Symbols.Count).SequenceEqual(rule.Symbols)))
                {
                    Rule existingMapping;
                    if (!result.TryGetValue(match, out existingMapping) || existingMapping.Symbols.Count < rule.Symbols.Count)
                    {
                        result[match] = rule;
                    }
                }
            }

            // require that all rules and all partial rules were mapped
            if (result.Count != toMap.Count) { return null; }
            if (this.rules[discriminator].Except(result.Values).Any()) { return null; }

            return result;
        }

        // to support initial prefix, we'll just add the ability to pass a null rule plus a suffix stack
        private ISet<IReadOnlyList<Symbol>> GatherPostTokenSuffixes(Token prefixToken, PartialRule rule)
        {
            var result = new HashSet<IReadOnlyList<Symbol>>();
            this.GatherPostTokenSuffixes(prefixToken, rule, ImmutableStack<Symbol>.Empty, result);
            return result;
        }

        /// <summary>
        /// Recursively gathers a set of <see cref="Symbol"/> lists which could form the remainder after consuming
        /// a <paramref name="prefixToken"/>
        /// </summary>
        private void GatherPostTokenSuffixes(
            Token prefixToken,
            PartialRule rule,
            ImmutableStack<Symbol> suffix,
            ISet<IReadOnlyList<Symbol>> result)
        {
            if (rule.Symbols.Count == 0)
            {
                if (!suffix.IsEmpty)
                {
                    var nextSuffixSymbol = suffix.Peek();
                    if (nextSuffixSymbol is Token)
                    {
                        if (nextSuffixSymbol == prefixToken)
                        {
                            result.Add(suffix.Skip(1).ToArray());
                        }
                    }
                    else
                    {
                        var newSuffix = suffix.Pop();
                        var innerRules = this.rules[(NonTerminal)nextSuffixSymbol]
                            .Where(r => this.firstFollow.NextOf(r).Contains(prefixToken));
                        foreach (var innerRule in innerRules)
                        {
                            this.GatherPostTokenSuffixes(prefixToken, new PartialRule(innerRule), newSuffix, result);
                        }
                    }
                }
                else
                {
                    throw new NotSupportedException("can't remove prefix from empty");
                } 
            }
            else if (rule.Symbols[0] is Token)
            {
                if (rule.Symbols[0] == prefixToken)
                {
                    result.Add(rule.Symbols.Skip(1).Concat(suffix).ToArray());
                }

                return;
            }
            else
            {
                // the new suffix adds the rest of the current rule, from back to front
                // to preserve ordering
                var newSuffix = suffix;
                for (var i = rule.Symbols.Count - 1; i > 0; --i)
                {
                    newSuffix = newSuffix.Push(rule.Symbols[i]);
                }

                var innerRules = this.rules[(NonTerminal)rule.Symbols[0]]
                    .Where(r => this.firstFollow.NextOf(r).Contains(prefixToken));
                foreach (var innerRule in innerRules)
                {
                    this.GatherPostTokenSuffixes(prefixToken, new PartialRule(innerRule), newSuffix, result);
                }
            }
        }

        private string DebugGrammar => string.Join(
            Environment.NewLine + Environment.NewLine,
            this.rules.Select(kvp => $"{kvp.Key}:{Environment.NewLine}" + string.Join(Environment.NewLine, kvp.Value.Select(r => "\t" + r)))
        );

        private class DiscriminatorPrefixInfo
        {
            public NonTerminal Symbol { get; set; }
            public Dictionary<Token, IParserNode> NodeCache { get; } = new Dictionary<Token, IParserNode>();
        }

        private class InternalFirstFollowProvider : IFirstFollowProvider
        {
            private readonly FirstFollowCalculator originalGrammarProvider;

            private readonly Dictionary<Symbol, IImmutableSet<Token>> additionalFirsts = new Dictionary<Symbol, IImmutableSet<Token>>();
            /// <summary>
            /// Note: since discriminator and discriminator prefix tokens never appear on the right-hand side of a rule, there is no reason
            /// to think about FOLLOW(TX). Instead, we should consider only FOLLOW(TX -> x) for each rule TX -> x. The advantage of this is
            /// that we get better differentiation: we can now distinguish between multiple nullable rules if they have different follows
            /// </summary>
            private readonly Dictionary<Rule, IImmutableSet<Token>> additionalFollows = new Dictionary<Rule, IImmutableSet<Token>>();

            public InternalFirstFollowProvider(FirstFollowCalculator provider)
            {
                this.originalGrammarProvider = provider;
            }

            public void Register(IReadOnlyDictionary<Rule, IImmutableSet<Token>> rulesToFollowSets)
            {
                var firstsBuilder = ImmutableHashSet.CreateBuilder<Token>();
                foreach (var rule in rulesToFollowSets.Keys)
                {
                    firstsBuilder.UnionWith(this.FirstOf(rule.Symbols));
                }

                var produced = rulesToFollowSets.Only(kvp => kvp.Key.Produced);
                this.additionalFirsts.Add(produced, firstsBuilder.ToImmutable());
                foreach (var kvp in rulesToFollowSets)
                {
                    this.additionalFollows.Add(kvp.Key, kvp.Value);
                }
            }

            public IImmutableSet<Token> FirstOf(Symbol symbol)
            {
                IImmutableSet<Token> additional;
                return this.additionalFirsts.TryGetValue(symbol, out additional)
                    ? additional
                    : this.originalGrammarProvider.FirstOf(symbol);
            }

            public IImmutableSet<Token> FollowOf(Symbol symbol)
            {
                if (this.additionalFirsts.ContainsKey(symbol))
                {
                    throw new ArgumentException(nameof(symbol), "Should not ask for follow of synthetic symbol " + symbol);
                }

                return this.originalGrammarProvider.FollowOf(symbol);
            }

            public IImmutableSet<Token> FollowOf(Rule rule)
            {
                IImmutableSet<Token> ruleFollowSet;
                return this.additionalFollows.TryGetValue(rule, out ruleFollowSet)
                    ? ruleFollowSet
                    : this.originalGrammarProvider.FollowOf(rule);
            }
        }
    }

    internal static class ParsingHelpers
    {
        public static TResult Only<TSource, TResult>(this IEnumerable<TSource> items, Func<TSource, TResult> selector, IEqualityComparer<TResult> comparer = null)
        {
            if (items == null) { throw new ArgumentNullException(nameof(items)); }
            if (selector == null) { throw new ArgumentNullException(nameof(selector)); }

            var comparerToUse = comparer ?? EqualityComparer<TResult>.Default;
            using (var enumerator = items.GetEnumerator())
            {
                if (!enumerator.MoveNext()) { throw new InvalidOperationException("The sequence contained no elements"); }

                var value = selector(enumerator.Current);

                while (enumerator.MoveNext())
                {
                    var otherValue = selector(enumerator.Current);
                    if (!comparerToUse.Equals(value, otherValue))
                    {
                        throw new InvalidOperationException($"The sequence contained multiple values: '{value}', '{otherValue}'");
                    }
                }

                return value;
            }
        }
    }
}
