using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>
    /// P2: the reflection runtime behind <c>call</c> and its discovery verbs. Pure managed
    /// reflection and NO Unity type is named here - everything game-shaped arrives through the
    /// delegates in <see cref="Protocol"/> that the game half installs, which is the only reason the
    /// offline self-check can compile and exercise the binder, the scorer, the handle table and the
    /// DTO caps with no game running.
    ///
    /// Main thread only. Every method here is reached from PPBridgeMain.Runner.Update, the handle
    /// table is unsynchronised on purpose, and the DTO it returns is fully projected before the pipe
    /// thread is allowed to see it.
    /// </summary>
    internal static class Reflect
    {
        // Caps. Every one of these is the difference between a verb that answers and a verb that
        // hangs the render loop or costs the agent thousands of tokens for one mistake.
        internal const int MaxHandles = 512;
        internal const int HandleTtlSeconds = 900;
        internal const int DefaultPageSize = 50;
        internal const int MaxPageSize = 200;
        internal const int MaxTypeResults = 100;
        internal const int MaxMemberResults = 400;
        internal const int MaxFindResults = 100;
        /// <summary>A response bigger than this is refused with advice, not truncated into a lie.</summary>
        internal const int MaxResponseBytes = 64 * 1024;

        // Overload scores, exactly as specified. Lower is better; a unique lowest wins and a TIE is
        // an error demanding an explicit `sig`, never "whatever reflection listed first".
        private const int ScoreExact = 0;
        private const int ScoreAssign = 1;
        private const int ScoreParse = 2;      // enum or guid parsed from a string/number
        private const int ScoreWiden = 3;      // lossless / range-checked numeric
        private const int Reject = int.MaxValue;

        // ------------------------------------------------------------------ handle table

        private sealed class Lease
        {
            internal object Target;
            internal DateTime Touched;
        }

        private static readonly Dictionary<int, Lease> leases = new Dictionary<int, Lease>();
        private static int epoch = 1;
        private static int nextId;

        /// <summary>
        /// Invalidates every outstanding handle at once. The game half calls this on scene unload:
        /// a handle to something that lived in the old scene must come back as a named refusal, not
        /// as a destroyed-object crash inside a later call.
        /// ponytail: defs are dropped along with everything else rather than pinned across epochs.
        /// They cost one cheap `find` to get back (it returns guids, not handles), so a pin list
        /// would be state to keep correct for no gain. Add pinning only if a def handle ever turns
        /// out to be expensive to re-resolve.
        /// </summary>
        internal static void NewEpoch()
        {
            leases.Clear();
            epoch++;
        }

        /// <summary>Leases an object and returns its handle string. Strong reference by design.</summary>
        internal static string Track(object target)
        {
            Prune();
            if (leases.Count >= MaxHandles)
            {
                // LRU: the oldest untouched lease goes, because refusing to hand out a handle would
                // strand the call that just produced a result.
                int oldest = 0;
                DateTime when = DateTime.MaxValue;
                foreach (KeyValuePair<int, Lease> kv in leases)
                    if (kv.Value.Touched < when) { when = kv.Value.Touched; oldest = kv.Key; }
                leases.Remove(oldest);
            }
            int id = ++nextId;
            leases[id] = new Lease { Target = target, Touched = DateTime.UtcNow };
            return "h:" + epoch + ":" + id;
        }

        private static void Prune()
        {
            DateTime cut = DateTime.UtcNow.AddSeconds(-HandleTtlSeconds);
            List<int> dead = null;
            foreach (KeyValuePair<int, Lease> kv in leases)
                if (kv.Value.Touched < cut) (dead ?? (dead = new List<int>())).Add(kv.Key);
            if (dead != null) foreach (int k in dead) leases.Remove(k);
        }

        /// <summary>
        /// Resolves a handle to its live target, or names exactly why it cannot. Three separate
        /// refusals on purpose - "expired", "from a previous epoch" and "the object was destroyed"
        /// are three different mistakes and an agent needs to tell them apart.
        /// </summary>
        internal static bool Resolve(string handle, out object target, out string error)
        {
            target = null;
            error = null;
            string[] p = (handle ?? "").Split(':');
            int e, id;
            if (p.Length != 3 || p[0] != "h" ||
                !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out e) ||
                !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                error = "'" + handle + "' is not a handle (expected h:<epoch>:<id>)";
                return false;
            }
            if (e != epoch)
            {
                error = "handle '" + handle + "' is from epoch " + e + ", the current epoch is " + epoch +
                        " (the scene changed; re-resolve it through roots/find)";
                return false;
            }
            Lease lease;
            if (!leases.TryGetValue(id, out lease))
            {
                error = "handle '" + handle + "' expired or was released";
                return false;
            }
            // Unity null semantics: a destroyed UnityEngine.Object is a live managed reference that
            // throws on nearly every use. The game half owns that test; offline there is nothing to
            // destroy and the hook is null.
            Func<object, bool> alive = Protocol.UnityAlive;
            if (alive != null && !alive(lease.Target))
            {
                leases.Remove(id);
                error = "handle '" + handle + "' points at a destroyed UnityEngine.Object";
                return false;
            }
            lease.Touched = DateTime.UtcNow;
            target = lease.Target;
            return true;
        }

        // ------------------------------------------------------------------ dispatch

        /// <summary>
        /// The P2 verbs. Returns null for a verb this file does not own, so Protocol can fall
        /// through to its own switch.
        /// </summary>
        internal static object Dispatch(string verb, JObject args)
        {
            object result;
            try
            {
                switch (verb)
                {
                    case "call": result = Call(args); break;
                    case "types": result = Types(args); break;
                    case "members": result = Members(args); break;
                    case "inspect": result = Inspect(args); break;
                    case "items": result = Items(args); break;
                    case "release": result = Release(args); break;
                    case "find": result = Find(args); break;
                    case "roots": result = Roots(); break;
                    default: return null;
                }
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                result = Threw(inner);
            }
            catch (Exception ex) { result = Threw(ex); }

            // The last cap, and the only one that can see the whole answer: a projection that is
            // individually within every other limit can still add up to a response an agent should
            // not be made to read.
            // BYTES, not chars: string.Length counts UTF-16 units, so a modded or localised def name
            // outside ASCII made the advertised byte cap wrong by up to 3x.
            string json = Protocol.Compact(result);
            int bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes <= MaxResponseBytes) return result;
            return Bad("cap", "the result projects to " + bytes + " bytes, over the " + MaxResponseBytes +
                              " byte cap - ask for a smaller page, a narrower filter, or a single member");
        }

        /// <summary>
        /// A game method that throws inside `call` used to answer with the type and message ALONE,
        /// and "NullReferenceException: Object reference not set to an instance of an object" names
        /// nothing at all - the one exception shape that carries no information in its message is
        /// also the commonest one a reflection driver provokes. The top frames say WHICH member of
        /// the game blew up, which is the whole question. Capped at 6 frames and clipped like every
        /// other string here, so a deep stack cannot become the response.
        /// </summary>
        private static object Threw(Exception ex)
        {
            string[] frames = (ex.StackTrace ?? "").Split('\n');
            List<string> top = new List<string>();
            for (int i = 0; i < frames.Length && top.Count < 6; i++)
            {
                string f = frames[i].Trim();
                if (f.Length > 0) top.Add(Protocol.Clip(f));
            }
            return new
            {
                ok = false,
                code = "threw",
                error = Protocol.Clip(ex.GetType().Name + ": " + ex.Message),
                at = top.ToArray()
            };
        }

        private static object Bad(string code, string message)
        {
            return new { ok = false, code, error = message };
        }

        // ------------------------------------------------------------------ type resolution

        private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        private static IEnumerable<Assembly> Assemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies();
        }

        /// <summary>
        /// Types are never guessed. An assembly-qualified name is taken as given; a bare name must
        /// match exactly one type across the loaded assemblies, and two matches are an ERROR listing
        /// both - picking one would silently call a method on the wrong type.
        /// </summary>
        internal static Type ResolveType(string name, string assembly, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name)) { error = "no type name given"; return null; }

            string key = name + "|" + assembly;
            Type cached;
            if (typeCache.TryGetValue(key, out cached)) return cached;

            if (name.IndexOf(',') >= 0)
            {
                Type qualified = Type.GetType(name, false);
                if (qualified == null) { error = "no type '" + name + "' (assembly-qualified name did not resolve)"; return null; }
                typeCache[key] = qualified;
                return qualified;
            }

            List<Type> hits = new List<Type>();
            foreach (Assembly asm in Assemblies())
            {
                if (!string.IsNullOrEmpty(assembly) &&
                    !string.Equals(asm.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase)) continue;
                Type[] all;
                try { all = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { all = ex.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                foreach (Type t in all)
                    if (t.FullName == name || t.Name == name) { if (!hits.Contains(t)) hits.Add(t); }
            }

            // An exact full-name match beats a bare-name one: `TacticalActor` and
            // `Something.TacticalActor` are not the same question.
            List<Type> exact = hits.Where(t => t.FullName == name).ToList();
            if (exact.Count > 0) hits = exact;

            if (hits.Count == 0) { error = "no type '" + name + "'" + (assembly == null ? "" : " in assembly '" + assembly + "'"); return null; }
            if (hits.Count > 1)
            {
                error = "'" + name + "' is ambiguous across " + hits.Count + " types: " +
                        string.Join(", ", hits.Take(5).Select(t => t.FullName + " [" + t.Assembly.GetName().Name + "]").ToArray()) +
                        " - name it fully or pass \"assembly\"";
                return null;
            }
            typeCache[key] = hits[0];
            return hits[0];
        }

        /// <summary>
        /// Every member declared anywhere in the hierarchy, base classes included. BindingFlags do
        /// NOT return inherited private members, so a chain walk with DeclaredOnly is the only way
        /// to reach a private field the game declared on a base class.
        /// </summary>
        private static List<T> Hierarchy<T>(Type type, Func<Type, T[]> get) where T : MemberInfo
        {
            List<T> all = new List<T>();
            for (Type t = type; t != null; t = t.BaseType) all.AddRange(get(t));
            return all;
        }

        private const BindingFlags AnyDeclared = BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.Instance | BindingFlags.Static |
                                                 BindingFlags.DeclaredOnly;

        // ------------------------------------------------------------------ call

        private static object Call(JObject a)
        {
            if (a == null) return Bad("args", "call needs {op, type|target, member, args}");
            string op = (string)a["op"];
            if (string.IsNullOrEmpty(op)) return Bad("args", "call needs an op: invoke, get, set or new");

            // Target first: an instance call resolves its type FROM the instance, so an explicit
            // "type" is only a filter for reaching a base-class member.
            object target = null;
            bool haveTarget = false;
            JToken targetTok = a["target"];
            if (targetTok != null && targetTok.Type != JTokenType.Null)
            {
                string error;
                if (!ResolveTarget(targetTok, out target, out error)) return Bad("handle", error);
                haveTarget = true;
            }

            Type type;
            string typeError;
            string typeName = (string)a["type"];
            if (!string.IsNullOrEmpty(typeName))
            {
                type = ResolveType(typeName, (string)a["assembly"], out typeError);
                if (type == null) return Bad("type", typeError);
                if (haveTarget && target != null && !type.IsInstanceOfType(target))
                    return Bad("type", "the target is a " + target.GetType().FullName + ", which is not a " + type.FullName);
            }
            else if (haveTarget && target != null) type = target.GetType();
            else return Bad("args", "call needs \"type\" (static) or \"target\" (instance)");

            JArray argsArr = a["args"] as JArray;
            string member = (string)a["member"];

            switch (op)
            {
                case "new": return New(type, argsArr);
                case "get": return GetSet(type, target, haveTarget, member, null, false, (string)a["convertTo"]);
                case "set":
                    if (a["value"] == null) return Bad("args", "set needs \"value\"");
                    return GetSet(type, target, haveTarget, member, a["value"], true);
                case "invoke": return Invoke(type, target, haveTarget, member, argsArr, a["sig"] as JArray, a["typeArgs"] as JArray);
                default: return Bad("op", "unknown op '" + op + "' (invoke, get, set, new)");
            }
        }

        /// <summary>
        /// A target is a handle, a root alias (<c>@tac</c>, re-resolved live every single time), or
        /// an argument envelope like <c>{"$h":"h:1:4"}</c>.
        /// </summary>
        private static bool ResolveTarget(JToken tok, out object target, out string error)
        {
            target = null;
            error = null;
            if (tok.Type == JTokenType.String)
            {
                string s = (string)tok;
                // A def is addressable BY NAME here so reading one field is one round trip instead of
                // three (find -> guid -> GetDef -> get). The prefix is explicit: a bare name could
                // collide with a root alias, and guessing which one was meant is how the wrong def
                // gets edited.
                if (s.StartsWith("@def:", StringComparison.OrdinalIgnoreCase)) return ResolveDef(s.Substring(5), out target, out error);
                if (s.StartsWith("@", StringComparison.Ordinal)) return ResolveRoot(s.Substring(1), out target, out error);
                return Resolve(s, out target, out error);
            }
            JObject o = tok as JObject;
            if (o != null && o["$h"] != null) return Resolve((string)o["$h"], out target, out error);
            if (o != null && o["$def"] != null) return ResolveDef((string)o["$def"], out target, out error);
            error = "target must be a handle \"h:e:i\", a root alias \"@name\", a def \"@def:<name|guid>\", " +
                    "or {\"$h\":...} / {\"$def\":...}";
            return false;
        }

        /// <summary>
        /// One def, named the way a human names it: an exact guid first (the repository's own O(1)
        /// lookup), else an EXACT def name, case-insensitive. Never a substring - <c>find</c> is the
        /// search verb, and a substring that happened to match two defs would silently pick one.
        /// </summary>
        internal static bool ResolveDef(string key, out object def, out string error)
        {
            def = null;
            error = null;
            if (string.IsNullOrEmpty(key)) { error = "a def reference needs a def name or a guid"; return false; }
            if (Protocol.DefByGuid != null)
            {
                def = Protocol.DefByGuid(key);
                if (def != null) return true;
            }
            if (Protocol.AllDefs == null)
            {
                error = "no def with guid '" + key + "', and no def repository to look a name up in";
                return false;
            }

            List<object> hits = new List<object>();
            foreach (object d in Protocol.AllDefs())
            {
                if (d == null) continue;
                string name, guid;
                if (!DefIdentity(d, d.GetType(), out name, out guid)) continue;
                if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add(d);
                if (hits.Count > 4) break;
            }
            if (hits.Count == 0)
            {
                error = "no def named '" + key + "' (an EXACT name, case-insensitive, or a guid) - " +
                        "use `find` to search by substring";
                return false;
            }
            if (hits.Count > 1)
            {
                error = "'" + key + "' names " + (hits.Count > 4 ? "at least 5" : hits.Count.ToString(CultureInfo.InvariantCulture)) +
                        " defs (" + string.Join(", ", hits.Select(h => h.GetType().Name).Distinct().Take(5).ToArray()) +
                        ") - pass its guid instead";
                return false;
            }
            def = hits[0];
            return true;
        }

        private static bool ResolveRoot(string alias, out object target, out string error)
        {
            target = null;
            error = null;
            if (Protocol.RootsProbe == null) { error = "no roots probe installed"; return false; }
            Dictionary<string, object> roots = Protocol.RootsProbe();
            if (roots == null || !roots.TryGetValue(alias, out target))
            {
                error = "no root '" + alias + "' (known: " +
                        (roots == null ? "none" : string.Join(", ", roots.Keys.ToArray())) + ")";
                return false;
            }
            if (target == null) { error = "root '" + alias + "' is null right now (wrong phase?)"; return false; }
            return true;
        }

        private static object New(Type type, JArray args)
        {
            // Constructors are never inherited, so this is the one lookup with no hierarchy walk.
            List<MethodBase> ctors = type.GetConstructors(AnyDeclared).Cast<MethodBase>().ToList();
            if (ctors.Count == 0)
            {
                // A struct's implicit parameterless constructor is not a ConstructorInfo, so a value
                // type with no explicit ctor lists none. "new" with no args on one means the default
                // instance - exactly what Activator.CreateInstance(Type) hands back.
                if (type.IsValueType && (args == null || args.Count == 0))
                    return Value(Activator.CreateInstance(type));
                return Bad("member", type.FullName + " has no accessible constructor");
            }
            object[] bound;
            object refusal = Pick(ctors, args, null, ".ctor", out bound);
            if (refusal != null) return refusal;
            ConstructorInfo chosen = (ConstructorInfo)Chosen;
            return Value(chosen.Invoke(bound));
        }

        private static object Invoke(Type type, object target, bool haveTarget, string member,
                                     JArray args, JArray sig, JArray typeArgs)
        {
            if (string.IsNullOrEmpty(member)) return Bad("args", "invoke needs \"member\"");
            List<MethodInfo> named = Hierarchy(type, t => t.GetMethods(AnyDeclared))
                                    .Where(m => m.Name == member).ToList();
            if (named.Count == 0) return Bad("member", "no method '" + member + "' on " + type.FullName + " or its bases");
            named = named.Where(m => m.IsStatic || haveTarget).ToList();
            if (named.Count == 0) return Bad("member", "'" + member + "' is an instance method and no target was given");

            // Open generics are never inferred: an inferred type argument that is merely plausible
            // calls a different method than the agent meant.
            List<MethodBase> candidates = new List<MethodBase>();
            List<string> genericRefusals = new List<string>();
            foreach (MethodInfo m in named)
            {
                if (!m.IsGenericMethodDefinition) { candidates.Add(m); continue; }
                if (typeArgs == null || typeArgs.Count == 0)
                {
                    genericRefusals.Add(Sig(m) + " is generic and needs \"typeArgs\"");
                    continue;
                }
                Type[] ta = new Type[typeArgs.Count];
                bool ok = true;
                for (int i = 0; i < ta.Length; i++)
                {
                    string e;
                    ta[i] = ResolveType((string)typeArgs[i], null, out e);
                    if (ta[i] == null) { genericRefusals.Add(e); ok = false; break; }
                }
                if (!ok) continue;
                if (m.GetGenericArguments().Length != ta.Length) { genericRefusals.Add(Sig(m) + " wants " + m.GetGenericArguments().Length + " typeArgs"); continue; }
                try { candidates.Add(m.MakeGenericMethod(ta)); }
                catch (Exception ex) { genericRefusals.Add(Sig(m) + ": " + ex.Message); }
            }
            if (candidates.Count == 0)
                return Bad("member", "no usable overload of '" + member + "': " + string.Join("; ", genericRefusals.ToArray()));

            object[] bound;
            object refusal = Pick(candidates, args, sig, member, out bound);
            if (refusal != null) return refusal;
            MethodInfo chosen = (MethodInfo)Chosen;
            object result = chosen.Invoke(chosen.IsStatic ? null : target, bound);
            if (chosen.ReturnType == typeof(void)) return new { ok = true, @void = true };
            return Value(result);
        }

        private static object GetSet(Type type, object target, bool haveTarget, string member, JToken value, bool write,
                                     string convertTo = null)
        {
            if (string.IsNullOrEmpty(member)) return Bad("args", "get/set needs \"member\"");

            PropertyInfo prop = Hierarchy(type, t => t.GetProperties(AnyDeclared)).FirstOrDefault(p => p.Name == member);
            FieldInfo field = prop != null ? null : Hierarchy(type, t => t.GetFields(AnyDeclared)).FirstOrDefault(f => f.Name == member);
            if (prop == null && field == null)
                return Bad("member", "no property or field '" + member + "' on " + type.FullName + " or its bases");

            Type memberType = prop != null ? prop.PropertyType : field.FieldType;
            bool isStatic = prop != null
                ? (prop.GetGetMethod(true) ?? prop.GetSetMethod(true)).IsStatic
                : field.IsStatic;
            if (!isStatic && !haveTarget) return Bad("member", "'" + member + "' is an instance member and no target was given");
            object self = isStatic ? null : target;

            // ponytail: indexers are not reachable through get/set - they need an argument list, i.e.
            // the invoke path. Call get_Item / set_Item with op:"invoke" if one is ever needed.
            if (prop != null && prop.GetIndexParameters().Length > 0)
                return Bad("member", "'" + member + "' is an indexer; invoke get_Item/set_Item instead");

            if (!write)
            {
                if (prop != null && prop.GetGetMethod(true) == null) return Bad("member", "'" + member + "' is write-only");
                object raw = prop != null ? prop.GetValue(self, null) : field.GetValue(self);
                if (string.IsNullOrEmpty(convertTo)) return Value(raw);
                // OPT-IN, and the ordinary projection comes back too: the caller asked for a number,
                // not for the struct to be replaced by one.
                string typeError2;
                Type dest = ResolveType(convertTo, null, out typeError2);
                if (dest == null) return Bad("type", typeError2);
                object converted;
                string convertError;
                if (!TryConvert(raw, dest, out converted, out convertError)) return Bad("convert", convertError);
                return new { ok = true, value = Project(raw), convertedTo = dest.FullName, converted = Project(converted) };
            }

            object bound;
            int score;
            string error;
            if (!BindArg(value, memberType, out bound, out score, out error))
                return Bad("bind", "cannot bind the value to " + memberType.FullName + ": " + error);
            if (prop != null)
            {
                if (prop.GetSetMethod(true) == null) return Bad("member", "'" + member + "' is read-only");
                prop.SetValue(self, bound, null);
            }
            else
            {
                if (field.IsInitOnly || field.IsLiteral) return Bad("member", "'" + member + "' is readonly/const");
                field.SetValue(self, bound);
            }
            return new { ok = true, set = member };
        }

        // ------------------------------------------------------------------ overload selection

        /// <summary>
        /// Set by <see cref="Pick"/> alongside the bound argument array. Single-threaded main-thread
        /// dispatch is what makes this legal; it exists so Pick can return a refusal DTO instead of
        /// forcing an out-parameter pair on every caller.
        /// </summary>
        private static MethodBase Chosen;

        /// <summary>
        /// Scores every candidate against the supplied arguments and demands a UNIQUE lowest score.
        /// A tie returns the tied signatures and refuses: two overloads that bind equally well are
        /// exactly the case where reflection order silently calls the wrong one.
        /// </summary>
        private static object Pick(List<MethodBase> candidates, JArray args, JArray sig, string member, out object[] bound)
        {
            bound = null;
            Chosen = null;
            int count = args == null ? 0 : args.Count;

            if (sig != null && sig.Count > 0)
            {
                string[] want = sig.Select(t => (string)t).ToArray();
                candidates = candidates.Where(m => Matches(m, want)).ToList();
                if (candidates.Count == 0)
                    return Bad("overload", "no overload matches sig [" + string.Join(", ", want) + "]");
            }

            List<string> why = new List<string>();
            List<MethodBase> best = new List<MethodBase>();
            object[] bestArgs = null;
            int bestScore = int.MaxValue;

            foreach (MethodBase m in candidates)
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != count) { why.Add(Sig(m) + " takes " + ps.Length + " args, " + count + " given"); continue; }

                object[] vals = new object[count];
                int total = 0;
                string refusal = null;
                for (int i = 0; i < count; i++)
                {
                    Type pt = ps[i].ParameterType;
                    // v1 rejects both outright: a byref or pointer parameter cannot be answered
                    // through a one-shot JSON request without an out-value protocol.
                    if (pt.IsByRef || pt.IsPointer)
                    {
                        refusal = Sig(m) + " has a by-ref or pointer parameter, which v1 refuses";
                        break;
                    }
                    object v;
                    int s;
                    string e;
                    if (!BindArg(args[i], pt, out v, out s, out e))
                    {
                        refusal = Sig(m) + " arg " + i + " (" + pt.Name + "): " + e;
                        break;
                    }
                    vals[i] = v;
                    total += s;
                }
                if (refusal != null) { why.Add(refusal); continue; }

                if (total < bestScore) { bestScore = total; best.Clear(); best.Add(m); bestArgs = vals; }
                else if (total == bestScore) best.Add(m);
            }

            if (best.Count == 0)
                return Bad("overload", "nothing binds" + (member == null ? "" : " for '" + member + "'") + ": " +
                                       string.Join("; ", why.Take(6).ToArray()));
            // An override and the base declaration it overrides reach here as two candidates with
            // the SAME signature and the same score, so neither a better argument nor `sig` can
            // separate them - `Wallet.ToString()` vs `Object.ToString()` is the one that surfaced
            // it, and it made every ToString/Equals/GetHashCode on an overriding type unreachable.
            // C# resolves that by the most-derived declaration; so does this.
            if (best.Count > 1) best = MostDerived(best);

            if (best.Count > 1)
                return new
                {
                    ok = false,
                    code = "ambiguous",
                    error = "score " + bestScore + " is a tie across " + best.Count +
                            " overloads - pass \"sig\" with the parameter type names",
                    candidates = best.Select(Sig).ToArray()
                };

            Chosen = best[0];
            bound = bestArgs;
            return null;
        }

        /// <summary>
        /// Drops every candidate whose declaring type is a BASE of another candidate's. Only
        /// hides a method that a more-derived type also declares, so a genuine overload tie
        /// (two different signatures on one type) still refuses.
        /// </summary>
        private static List<MethodBase> MostDerived(List<MethodBase> ms)
        {
            List<MethodBase> keep = new List<MethodBase>();
            foreach (MethodBase m in ms)
            {
                bool shadowed = false;
                foreach (MethodBase o in ms)
                {
                    if (o == m || m.DeclaringType == null || o.DeclaringType == null) continue;
                    if (m.DeclaringType != o.DeclaringType && m.DeclaringType.IsAssignableFrom(o.DeclaringType))
                    { shadowed = true; break; }
                }
                if (!shadowed) keep.Add(m);
            }
            return keep.Count == 0 ? ms : keep;
        }

        private static bool Matches(MethodBase m, string[] want)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length != want.Length) return false;
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                if (!string.Equals(pt.FullName, want[i], StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(pt.Name, want[i], StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        internal static string Sig(MethodBase m)
        {
            string ret = m is MethodInfo ? ((MethodInfo)m).ReturnType.Name + " " : "";
            return (m.IsStatic ? "static " : "") + ret + m.Name +
                   "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name).ToArray()) + ")";
        }

        // ------------------------------------------------------------------ argument binding

        /// <summary>
        /// One JSON token into one parameter type, with the score that decides the overload. Every
        /// conversion is explicit: no user-defined operators, no string coercion of numbers, and no
        /// numeric conversion that could silently lose an integer.
        /// </summary>
        internal static bool BindArg(JToken tok, Type target, out object value, out int score, out string error)
        {
            value = null;
            score = Reject;
            error = null;

            if (tok == null || tok.Type == JTokenType.Null)
            {
                if (target.IsValueType && Nullable.GetUnderlyingType(target) == null)
                {
                    error = "null cannot bind to the value type " + target.Name;
                    return false;
                }
                score = ScoreAssign;
                return true;
            }

            Type under = Nullable.GetUnderlyingType(target);
            if (under != null) return BindArg(tok, under, out value, out score, out error);

            JObject env = tok as JObject;
            if (env != null)
            {
                string tag = env.Properties().Select(p => p.Name).FirstOrDefault(n => n.Length > 1 && n[0] == '$');
                if (tag != null) return BindEnvelope(tag, env, target, out value, out score, out error);
                error = "a JSON object argument must be a tagged envelope ($h, $enum, $type, $def, $array, $v2, $v3, $quat)";
                return false;
            }

            if (tok.Type == JTokenType.Array)
            {
                // A bare array binds as $array when the parameter says what the elements are.
                return BindArray((JArray)tok, target, null, out value, out score, out error);
            }

            if (tok.Type == JTokenType.Boolean)
            {
                if (target == typeof(bool)) { value = (bool)tok; score = ScoreExact; return true; }
                if (target == typeof(object)) { value = (bool)tok; score = ScoreAssign; return true; }
                error = "a boolean cannot bind to " + target.Name;
                return false;
            }

            if (tok.Type == JTokenType.String)
            {
                string s = (string)tok;
                if (target == typeof(string)) { value = s; score = ScoreExact; return true; }
                if (target == typeof(object)) { value = s; score = ScoreAssign; return true; }
                if (target.IsEnum) return BindEnum(s, target, out value, out score, out error);
                if (target == typeof(Guid))
                {
                    Guid g;
                    if (!Guid.TryParse(s, out g)) { error = "'" + s + "' is not a Guid"; return false; }
                    value = g; score = ScoreParse; return true;
                }
                if (target == typeof(char))
                {
                    if (s.Length != 1) { error = "a char needs a one-character string"; return false; }
                    value = s[0]; score = ScoreParse; return true;
                }
                // Deliberately NOT parsed into a number: "12" reaching an int parameter is far more
                // often a mistake than an intention, and silence there is how the wrong call happens.
                error = "a string cannot bind to " + target.Name;
                return false;
            }

            if (tok.Type == JTokenType.Integer || tok.Type == JTokenType.Float)
                return BindNumber(tok, target, out value, out score, out error);

            error = "a JSON " + tok.Type + " cannot bind to " + target.Name;
            return false;
        }

        private static bool BindEnum(string text, Type target, out object value, out int score, out string error)
        {
            value = null;
            score = Reject;
            error = null;
            try { value = Enum.Parse(target, text, true); }
            catch (Exception)
            {
                error = "'" + text + "' is not a " + target.Name + " (values: " +
                        string.Join(", ", Enum.GetNames(target).Take(20).ToArray()) + ")";
                return false;
            }
            score = ScoreParse;
            return true;
        }

        /// <summary>
        /// Numbers are the one place a silent conversion is a real bug, so widening is checked by
        /// VALUE, not merely by type: a JSON integer binds to a narrower integer parameter only when
        /// it round-trips exactly.
        /// </summary>
        private static bool BindNumber(JToken tok, Type target, out object value, out int score, out string error)
        {
            value = null;
            score = Reject;
            error = null;

            if (target == typeof(object))
            {
                value = tok.Type == JTokenType.Integer ? (object)(long)tok : (object)(double)tok;
                score = ScoreAssign;
                return true;
            }
            if (target.IsEnum)
            {
                if (tok.Type != JTokenType.Integer) { error = "an enum needs an integer or a name"; return false; }
                value = Enum.ToObject(target, (long)tok);
                score = ScoreParse;
                return true;
            }

            if (tok.Type == JTokenType.Integer)
            {
                long v = (long)tok;
                if (target == typeof(long)) { value = v; score = ScoreExact; return true; }
                if (target == typeof(int) || target == typeof(short) || target == typeof(sbyte) ||
                    target == typeof(byte) || target == typeof(ushort) || target == typeof(uint) ||
                    target == typeof(ulong))
                {
                    try
                    {
                        object narrowed = Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
                        // The round trip is the check: OverflowException catches the range, and this
                        // catches everything ChangeType would round instead of refusing.
                        if (Convert.ToInt64(narrowed, CultureInfo.InvariantCulture) != v)
                        {
                            error = v + " does not survive the trip to " + target.Name;
                            return false;
                        }
                        value = narrowed;
                        score = target == typeof(int) ? ScoreWiden : ScoreWiden;
                        return true;
                    }
                    catch (OverflowException) { error = v + " is out of range for " + target.Name; return false; }
                    catch (Exception ex) { error = ex.Message; return false; }
                }
                if (target == typeof(float)) { value = (float)v; score = ScoreWiden; return true; }
                if (target == typeof(double)) { value = (double)v; score = ScoreWiden; return true; }
                if (target == typeof(decimal)) { value = (decimal)v; score = ScoreWiden; return true; }
                error = "an integer cannot bind to " + target.Name;
                return false;
            }

            double d = (double)tok;
            if (target == typeof(double)) { value = d; score = ScoreExact; return true; }
            // ponytail: double -> float is formally narrowing and is allowed anyway, at widening
            // score. JSON has exactly one fractional number type and virtually every game API takes
            // float, so the strict rule would refuse `12.5` for a Vector3 component. Integers, where
            // a silent truncation is a genuine bug, stay range-checked above.
            if (target == typeof(float)) { value = (float)d; score = ScoreWiden; return true; }
            if (target == typeof(decimal)) { value = (decimal)d; score = ScoreWiden; return true; }
            error = "a fractional number cannot bind to " + target.Name;
            return false;
        }

        private static bool BindEnvelope(string tag, JObject env, Type target, out object value, out int score, out string error)
        {
            value = null;
            score = Reject;
            error = null;
            JToken payload = env[tag];

            switch (tag)
            {
                case "$h":
                {
                    object obj;
                    if (!Resolve((string)payload, out obj, out error)) return false;
                    if (obj != null && !target.IsInstanceOfType(obj))
                    {
                        error = "the handle is a " + obj.GetType().FullName + ", not a " + target.FullName;
                        return false;
                    }
                    value = obj;
                    score = obj != null && obj.GetType() == target ? ScoreExact : ScoreAssign;
                    return true;
                }
                case "$def":
                {
                    // A guid OR an exact def name: the guid hop exists because `find` returns one,
                    // not because the binder needs it.
                    object def;
                    if (!ResolveDef((string)payload, out def, out error)) return false;
                    if (!target.IsInstanceOfType(def))
                    {
                        error = "def '" + (string)payload + "' is a " + def.GetType().Name + ", not a " + target.Name;
                        return false;
                    }
                    value = def;
                    score = def.GetType() == target ? ScoreExact : ScoreAssign;
                    return true;
                }
                case "$type":
                {
                    Type t = ResolveType((string)payload, (string)env["assembly"], out error);
                    if (t == null) return false;
                    if (!target.IsAssignableFrom(typeof(Type))) { error = target.Name + " does not take a System.Type"; return false; }
                    value = t;
                    score = target == typeof(Type) ? ScoreExact : ScoreAssign;
                    return true;
                }
                case "$enum":
                {
                    Type t = target;
                    string named = (string)env["type"];
                    if (!string.IsNullOrEmpty(named)) { t = ResolveType(named, (string)env["assembly"], out error); if (t == null) return false; }
                    if (!t.IsEnum) { error = t.Name + " is not an enum"; return false; }
                    if (!(payload.Type == JTokenType.String
                              ? BindEnum((string)payload, t, out value, out score, out error)
                              : BindNumber(payload, t, out value, out score, out error))) return false;
                    if (t != target && !target.IsInstanceOfType(value)) { error = t.Name + " does not bind to " + target.Name; return false; }
                    return true;
                }
                case "$array":
                {
                    JArray items = payload as JArray;
                    if (items == null) { error = "$array needs a JSON array"; return false; }
                    return BindArray(items, target, (string)env["type"], out value, out score, out error);
                }
                case "$v2": return BindVector(payload as JArray, target, 2, "UnityEngine.Vector2", out value, out score, out error);
                case "$v3": return BindVector(payload as JArray, target, 3, "UnityEngine.Vector3", out value, out score, out error);
                case "$quat": return BindVector(payload as JArray, target, 4, "UnityEngine.Quaternion", out value, out score, out error);
                default:
                    error = "unknown envelope '" + tag + "'";
                    return false;
            }
        }

        private static bool BindArray(JArray items, Type target, string elementTypeName, out object value, out int score, out string error)
        {
            value = null;
            score = Reject;
            error = null;

            Type element = null;
            if (!string.IsNullOrEmpty(elementTypeName))
            {
                element = ResolveType(elementTypeName, null, out error);
                if (element == null) return false;
            }
            else if (target.IsArray) element = target.GetElementType();
            else if (target.IsGenericType && target.GetGenericArguments().Length == 1) element = target.GetGenericArguments()[0];
            if (element == null) { error = "cannot tell the element type for " + target.Name + " - pass \"type\" in the $array envelope"; return false; }

            Array made = Array.CreateInstance(element, items.Count);
            int worst = ScoreExact;
            for (int i = 0; i < items.Count; i++)
            {
                object v;
                int s;
                string e;
                if (!BindArg(items[i], element, out v, out s, out e)) { error = "element " + i + ": " + e; return false; }
                made.SetValue(v, i);
                if (s > worst) worst = s;
            }

            if (target.IsInstanceOfType(made)) { value = made; score = worst; return true; }
            // List<T> and friends: a one-argument constructor taking IEnumerable<T> is the shape the
            // BCL collections all share, and it is the only one attempted.
            ConstructorInfo ctor = target.IsAbstract || target.IsInterface ? null
                : target.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(element) });
            if (ctor == null) { error = "cannot build a " + target.Name + " from an array of " + element.Name; return false; }
            value = ctor.Invoke(new object[] { made });
            score = worst > ScoreAssign ? worst : ScoreAssign;
            return true;
        }

        /// <summary>
        /// A vector envelope binds by CONSTRUCTING the parameter's own type from N floats, which is
        /// what makes it generic (and offline-testable). It falls back to the named Unity type only
        /// when the parameter is loose, e.g. an <c>object</c>.
        /// </summary>
        private static bool BindVector(JArray nums, Type target, int arity, string unityType,
                                       out object value, out int score, out string error)
        {
            value = null;
            score = Reject;
            error = null;
            if (nums == null || nums.Count != arity) { error = "expected " + arity + " numbers"; return false; }

            float[] f = new float[arity];
            for (int i = 0; i < arity; i++)
            {
                if (nums[i].Type != JTokenType.Integer && nums[i].Type != JTokenType.Float) { error = "component " + i + " is not a number"; return false; }
                f[i] = (float)(double)nums[i];
            }

            Type build = target;
            bool exact = true;
            ConstructorInfo ctor = FloatCtor(build, arity);
            if (ctor == null)
            {
                build = ResolveType(unityType, null, out error);
                if (build == null) { error = "no constructor taking " + arity + " floats on " + target.Name + ", and " + unityType + " is not loaded"; return false; }
                ctor = FloatCtor(build, arity);
                if (ctor == null) { error = unityType + " has no " + arity + "-float constructor"; return false; }
                exact = false;
                if (!target.IsAssignableFrom(build)) { error = build.Name + " does not bind to " + target.Name; return false; }
            }
            object[] boxed = new object[arity];
            for (int i = 0; i < arity; i++) boxed[i] = f[i];
            value = ctor.Invoke(boxed);
            score = exact ? ScoreExact : ScoreAssign;
            return true;
        }

        private static ConstructorInfo FloatCtor(Type t, int arity)
        {
            if (t == null || t.IsInterface || t.IsAbstract) return null;
            Type[] sig = new Type[arity];
            for (int i = 0; i < arity; i++) sig[i] = typeof(float);
            return t.GetConstructor(sig);
        }

        // ------------------------------------------------------------------ result projection

        private static object Value(object v)
        {
            return new { ok = true, value = Project(v) };
        }

        /// <summary>
        /// Turns one live object into a plain DTO. This NEVER enumerates, NEVER walks properties and
        /// NEVER calls an arbitrary ToString - a non-trivial reference becomes a handle plus its
        /// type, and a collection becomes a handle plus a count that `items` can page. That refusal
        /// to be helpful is the whole reason a response cannot cost thousands of tokens or hang the
        /// game inside somebody's getter.
        ///
        /// ponytail: depth is structurally 1 - the only nesting that exists is a known value type's
        /// own primitive fields - so there is no depth counter to keep. Add one the day anything
        /// here recurses.
        /// </summary>
        internal static object Project(object v)
        {
            if (v == null) return null;
            Type t = v.GetType();

            if (v is Type) return new Dictionary<string, object> { { "$type", ((Type)v).FullName }, { "assembly", ((Type)v).Assembly.GetName().Name } };

            object scalar;
            if (TryScalar(v, out scalar)) return scalar;

            Dictionary<string, object> dto = new Dictionary<string, object>
            {
                { "h", Track(v) },
                { "type", t.FullName }
            };

            int? count = CountOf(v);
            if (count != null)
            {
                dto["count"] = count.Value;
                dto["collection"] = true;
            }

            // Only two identity reads, both on types whose accessors are known side-effect-free.
            Type unityObject = UnityObjectType();
            if (unityObject != null && unityObject.IsInstanceOfType(v))
            {
                PropertyInfo name = unityObject.GetProperty("name");
                MethodInfo id = unityObject.GetMethod("GetInstanceID", Type.EmptyTypes);
                try
                {
                    if (name != null) dto["name"] = Protocol.Clip((string)name.GetValue(v, null));
                    if (id != null) dto["instanceId"] = id.Invoke(v, null);
                }
                catch (Exception) { /* a half-destroyed object still gets a usable handle */ }
                FieldInfo guid = t.GetField("Guid");
                if (guid != null && guid.FieldType == typeof(string))
                {
                    try { dto["guid"] = (string)guid.GetValue(v); } catch (Exception) { }
                }
            }
            return dto;
        }

        /// <summary>
        /// Everything that projects to a value rather than to a handle: primitives, strings, enums,
        /// the three round-trippable structs, and a small inlineable value type. Split out of
        /// <see cref="Project"/> so a bulk read can ask "would this be a scalar?" WITHOUT minting a
        /// handle for every answer of "no".
        /// </summary>
        private static bool TryScalar(object v, out object dto)
        {
            dto = null;
            if (v == null) return false;
            Type t = v.GetType();
            if (t.IsEnum) { dto = new Dictionary<string, object> { { "$enum", v.ToString() }, { "type", t.FullName } }; return true; }
            if (v is string) { dto = Protocol.Clip((string)v); return true; }
            if (t.IsPrimitive) { dto = v; return true; }
            if (v is decimal) { dto = (double)(decimal)v; return true; }
            // A whitelist, not a walk: these three have no meaningful handle form and their ToString
            // is a documented round-trippable format rather than an arbitrary override.
            if (v is Guid || v is DateTime || v is TimeSpan) { dto = Convert.ToString(v, CultureInfo.InvariantCulture); return true; }
            return TryInlineStruct(v, t, out dto);
        }

        /// <summary>
        /// A small value type whose public fields are all primitive is projected inline - that is
        /// how Vector3 comes back readable instead of as a handle. Fields are READ, never getters,
        /// and the shape is capped at four so nothing large sneaks through this door.
        /// </summary>
        private static bool TryInlineStruct(object v, Type t, out object dto)
        {
            dto = null;
            if (!t.IsValueType || t.IsEnum || t.IsPrimitive) return false;
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length == 0 || fields.Length > 4) return false;
            foreach (FieldInfo f in fields) if (!f.FieldType.IsPrimitive) return false;
            Dictionary<string, object> d = new Dictionary<string, object> { { "type", t.FullName } };
            foreach (FieldInfo f in fields) d[f.Name] = f.GetValue(v);
            dto = d;
            return true;
        }

        /// <summary>
        /// A user-defined conversion, run ONLY because a caller named the destination type
        /// (<c>call {"op":"get", …, "convertTo":"System.Single"}</c>). `wp.Value` on a
        /// Base.Entities.Statuses.ModifiableValue is a float through an implicit operator, and this
        /// endpoint hands back the struct - the conversion is how a reader gets the same number.
        ///
        /// It is OPT-IN because running an operator is running arbitrary user code: it may allocate,
        /// mutate, log, block or throw, and projection's whole safety story is that it reads fields.
        /// Found generically - <c>op_Implicit</c>/<c>op_Explicit</c> declared on EITHER the source or
        /// the destination type, which is where C# looks - and no type is named here. Exactly one
        /// candidate is an answer; two is a question this file refuses to settle.
        /// </summary>
        private static bool TryConvert(object v, Type dest, out object result, out string error)
        {
            result = null;
            error = null;
            if (v == null) { error = "the value is null, so there is nothing to convert"; return false; }
            Type src = v.GetType();
            if (dest.IsInstanceOfType(v)) { result = v; return true; }

            List<MethodInfo> hits = new List<MethodInfo>();
            foreach (Type owner in new Type[] { src, dest })
                foreach (MethodInfo m in owner.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "op_Implicit" && m.Name != "op_Explicit") continue;
                    if (m.ReturnType != dest) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsAssignableFrom(src)) continue;
                    if (!hits.Contains(m)) hits.Add(m);
                }
            if (hits.Count == 0)
            {
                error = "no user-defined conversion from " + src.FullName + " to " + dest.FullName +
                        " is declared on either type";
                return false;
            }
            if (hits.Count > 1)
            {
                error = hits.Count + " conversions from " + src.FullName + " to " + dest.FullName +
                        " - name a different destination type";
                return false;
            }
            try { result = hits[0].Invoke(null, new object[] { v }); return true; }
            catch (Exception ex)
            {
                error = "the conversion operator threw: " + ex.GetType().Name + ": " +
                        (ex.InnerException == null ? ex.Message : ex.InnerException.Message);
                return false;
            }
        }

        /// <summary>Count without enumerating: only from a collection that already knows its size.</summary>
        private static int? CountOf(object v)
        {
            Array arr = v as Array;
            if (arr != null) return arr.Length;
            ICollection c = v as ICollection;
            if (c != null) return c.Count;
            foreach (Type i in v.GetType().GetInterfaces())
            {
                if (!i.IsGenericType || i.GetGenericTypeDefinition() != typeof(ICollection<>)) continue;
                try { return (int)i.GetProperty("Count").GetValue(v, null); }
                catch (Exception) { return null; }
            }
            // An IEnumerable with no Count is still a collection, but its size is unknown until
            // somebody asks `items` for a page - which is the point.
            return v is IEnumerable && !(v is string) ? (int?)null : null;
        }

        private static Type unityObjectType;
        private static bool unityObjectLooked;

        private static Type UnityObjectType()
        {
            if (unityObjectLooked) return unityObjectType;
            unityObjectLooked = true;
            string e;
            unityObjectType = ResolveType("UnityEngine.Object", "UnityEngine.CoreModule", out e);
            return unityObjectType;
        }

        // ------------------------------------------------------------------ discovery verbs

        private static object Types(JObject a)
        {
            string pattern = a == null ? null : (string)a["pattern"];
            if (string.IsNullOrEmpty(pattern)) return Bad("args", "types needs {pattern}");
            string wantAsm = a == null ? null : (string)a["assembly"];

            List<string> hits = new List<string>();
            bool more = false;
            foreach (Assembly asm in Assemblies())
            {
                string an = asm.GetName().Name;
                if (!string.IsNullOrEmpty(wantAsm) && !string.Equals(an, wantAsm, StringComparison.OrdinalIgnoreCase)) continue;
                Type[] all;
                try { all = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { all = ex.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                foreach (Type t in all)
                {
                    if (t.FullName == null || t.FullName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (hits.Count >= MaxTypeResults) { more = true; break; }
                    hits.Add(t.FullName + " [" + an + "]");
                }
                if (more) break;
            }
            return new { ok = true, count = hits.Count, truncated = more, types = hits.ToArray() };
        }

        private static object Members(JObject a)
        {
            if (a == null) return Bad("args", "members needs {type} or {h}");
            Type type;
            string error;
            // Same target grammar as `inspect`: a handle, a root alias, or a def by name. Refusing
            // "@def:X" here while inspect accepted it was a difference with no reason behind it.
            JToken tok = a["h"];
            if (tok != null && tok.Type != JTokenType.Null)
            {
                object target;
                if (!ResolveTarget(tok, out target, out error)) return Bad("handle", error);
                type = target.GetType();
            }
            else
            {
                type = ResolveType((string)a["type"], (string)a["assembly"], out error);
                if (type == null) return Bad("type", error);
            }
            return MembersOf(type, a, null, null);
        }

        private static object Inspect(JObject a)
        {
            JToken tok = a == null ? null : a["h"];
            if (tok == null || tok.Type == JTokenType.Null)
                return Bad("args", "inspect needs {h}: a handle \"h:e:i\", a root alias \"@tac\", or a def \"@def:<name|guid>\"");
            object target;
            string error;
            if (!ResolveTarget(tok, out target, out error)) return Bad("handle", error);
            // Project first: it refreshes the lease and gives back the same identity fields any
            // other result would carry, so `inspect` and a call result describe an object the same way.
            return MembersOf(target.GetType(), a, Project(target), target);
        }

        /// <summary>
        /// A member filter is a plain substring, or a glob when it carries <c>*</c>/<c>?</c> - so
        /// "members matching *Coefficient*" is askable instead of guessable. Matched against the
        /// whole formatted line for the member list, and against the bare name for a value dump.
        /// </summary>
        private static bool MemberMatches(string text, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            if (filter.IndexOf('*') < 0 && filter.IndexOf('?') < 0)
                return text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            Regex rx;
            if (!globCache.TryGetValue(filter, out rx))
            {
                rx = new Regex("^" + Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                globCache[filter] = rx;
            }
            return rx.IsMatch(text);
        }

        private static readonly Dictionary<string, Regex> globCache = new Dictionary<string, Regex>();

        /// <summary>
        /// EVERY instance field of one live object, each with an explicit status. This is what makes
        /// two runs diffable: no handles (their ids differ every session), so the same object read
        /// twice produces byte-identical JSON unless a value actually changed.
        ///
        /// NOTHING IS SILENTLY OMITTED. A field that is not a scalar is the marker
        /// <c>{"$omitted":type[,count][,guid]}</c>, a null field is JSON <c>null</c>, and a read that
        /// threw is <c>{"$error":exceptionType}</c> - because in a mechanical diff "unchanged" and
        /// "not observed" must never look the same.
        ///
        /// ponytail: FIELDS only. A property is a getter and this file's whole projection contract is
        /// that it never calls one; def data is fields anyway. The marker carries type, collection
        /// count and def guid - all field reads - and no handle; `get` that member by name when you
        /// want the object itself.
        /// </summary>
        private static object ValuesOf(Type type, object instance, string filter)
        {
            SortedDictionary<string, object> values = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (FieldInfo f in Hierarchy(type, t => t.GetFields(AnyDeclared)))
            {
                if (f.IsStatic) continue;
                // An auto-property's backing field is reported under the PROPERTY's name: it is the
                // property's value, read without ever calling a getter, and "<name>k__BackingField"
                // is not a name anyone can look up.
                string name = f.Name;
                int close = name.IndexOf(">k__BackingField", StringComparison.Ordinal);
                if (name.Length > 0 && name[0] == '<' && close > 0) name = name.Substring(1, close - 1);
                if (values.ContainsKey(name)) continue;   // a derived field shadows its base
                if (!MemberMatches(name, filter)) continue;
                object raw;
                try { raw = f.GetValue(instance); }
                catch (Exception ex) { values[name] = new Dictionary<string, object> { { "$error", ex.GetType().Name } }; continue; }
                if (raw == null) { values[name] = null; continue; }
                object dto;
                // Asked BEFORE projecting: Project would mint a handle for every non-scalar field and
                // fill the lease table with objects nobody asked for.
                values[name] = TryScalar(raw, out dto) ? dto : Omitted(raw);
            }
            return values;
        }

        /// <summary>
        /// The stand-in for a field the dump will not read: its type, plus the two identities that do
        /// NOT move between sessions - a known collection size and a def's guid, both read as fields.
        /// No handle, no getter, nothing session-scoped: this line has to be byte-identical on a
        /// second run of an unchanged object.
        /// </summary>
        private static object Omitted(object v)
        {
            Dictionary<string, object> d = new Dictionary<string, object> { { "$omitted", v.GetType().FullName } };
            int? count = CountOf(v);
            if (count != null) d["count"] = count.Value;
            FieldInfo guid = v.GetType().GetField("Guid");
            if (guid != null && guid.FieldType == typeof(string))
            {
                try { d["guid"] = (string)guid.GetValue(v); } catch (Exception) { }
            }
            return d;
        }

        private static object MembersOf(Type type, JObject a, object self, object instance)
        {
            string filter = a == null ? null : (string)a["filter"];
            int page = a == null || a["page"] == null ? 0 : (int)a["page"];
            int size = a == null || a["pageSize"] == null ? MaxMemberResults : (int)a["pageSize"];
            JToken valuesTok = a == null ? null : a["values"];
            bool wantValues = valuesTok != null && valuesTok.Type == JTokenType.Boolean && (bool)valuesTok;
            if (page < 0) return Bad("args", "page must be >= 0");
            if (size < 1 || size > MaxMemberResults) return Bad("args", "pageSize must be 1.." + MaxMemberResults);
            if (wantValues && instance == null)
                return Bad("args", "values:true needs a live object - ask `inspect`, not `members`");

            List<string> lines = new List<string>();
            Action<string> add = s => { if (MemberMatches(s, filter)) lines.Add(s); };

            foreach (ConstructorInfo c in type.GetConstructors(AnyDeclared))
                add("C " + Sig(c));
            foreach (PropertyInfo p in Hierarchy(type, t => t.GetProperties(AnyDeclared)))
                add("P " + p.PropertyType.Name + " " + p.Name +
                    " {" + (p.GetGetMethod(true) != null ? "get;" : "") + (p.GetSetMethod(true) != null ? "set;" : "") + "}" +
                    (p.DeclaringType == type ? "" : " <" + p.DeclaringType.Name + ">"));
            foreach (FieldInfo f in Hierarchy(type, t => t.GetFields(AnyDeclared)))
                add("F " + (f.IsStatic ? "static " : "") + f.FieldType.Name + " " + f.Name +
                    (f.DeclaringType == type ? "" : " <" + f.DeclaringType.Name + ">"));
            foreach (MethodInfo m in Hierarchy(type, t => t.GetMethods(AnyDeclared)))
            {
                if (m.IsSpecialName) continue;   // property/event accessors are already listed above
                add("M " + Sig(m) + (m.DeclaringType == type ? "" : " <" + m.DeclaringType.Name + ">"));
            }

            // PAGED, not silently cut. The old cap dropped everything past 400 with a bare
            // "truncated":true and no way to ask for the rest.
            long skip = (long)page * size;
            if (skip > lines.Count) skip = lines.Count;
            List<string> shown = new List<string>();
            for (int i = (int)skip; i < lines.Count && shown.Count < size; i++) shown.Add(lines[i]);
            bool more = skip + shown.Count < lines.Count;

            Dictionary<string, object> dto = new Dictionary<string, object>
            {
                { "ok", true },
                { "type", type.FullName },
                { "assembly", type.Assembly.GetName().Name },
                { "baseType", type.BaseType == null ? null : type.BaseType.FullName },
                { "self", self },
                { "count", shown.Count },
                { "total", lines.Count },
                { "page", page },
                { "pageSize", size },
                { "truncated", more },
                { "hasMore", more }
            };
            // The member LIST is the shape and the value dump is the data; asking for both is how a
            // def dump hits the 64 KB cap and comes back as a refusal instead of a table.
            if (wantValues)
            {
                // A value dump exists to be DIFFED, and `self` carried the two fields that make two
                // identical reads differ anyway: a handle is minted fresh on every call, and an
                // instanceId is new every session. The name and guid stay - they identify the object
                // and they do not move.
                Dictionary<string, object> id = self as Dictionary<string, object>;
                if (id != null) { id.Remove("h"); id.Remove("instanceId"); }
                dto["values"] = ValuesOf(type, instance, filter);
            }
            else dto["members"] = shown.ToArray();
            return dto;
        }

        private static object Items(JObject a)
        {
            string handle = a == null ? null : (string)a["h"];
            object target;
            string error;
            if (!Resolve(handle, out target, out error)) return Bad("handle", error);

            IEnumerable seq = target as IEnumerable;
            if (seq == null || target is string) return Bad("args", handle + " is a " + target.GetType().Name + ", which is not enumerable");

            int page = a["page"] == null ? 0 : (int)a["page"];
            int size = a["pageSize"] == null ? DefaultPageSize : (int)a["pageSize"];
            if (page < 0) return Bad("args", "page must be >= 0");
            if (size < 1 || size > MaxPageSize) return Bad("args", "pageSize must be 1.." + MaxPageSize);

            int skip = page * size;
            List<object> items = new List<object>();
            bool hasMore = false;

            // The only place this file enumerates anything, and only because it was asked to by name.
            IList list = target as IList;
            if (list != null)
            {
                for (int i = skip; i < list.Count && items.Count < size; i++) items.Add(Project(list[i]));
                hasMore = skip + items.Count < list.Count;
            }
            else
            {
                int seen = 0;
                foreach (object o in seq)
                {
                    if (seen++ < skip) continue;
                    if (items.Count >= size) { hasMore = true; break; }
                    items.Add(Project(o));
                }
            }
            // `count` is OMITTED, not emitted empty, when the source does not know its own size - a
            // lazy iterator (TacticalMap+<GetTacActors>d__61 is the one that surfaced this) genuinely
            // has no count until it is walked, and `"count":null` reads as "zero items" to a client.
            // The page fields below are always true, so an agent is never left without an answer.
            Dictionary<string, object> dto = new Dictionary<string, object>
            {
                { "ok", true }, { "page", page }, { "pageSize", size },
                { "returned", items.Count }, { "hasMore", hasMore }
            };
            int? total = CountOf(target);
            if (total != null) dto["count"] = total.Value;
            dto["items"] = items.ToArray();
            return dto;
        }

        private static object Release(JObject a)
        {
            string handle = a == null ? null : (string)a["h"];
            string[] p = (handle ?? "").Split(':');
            int e, id;
            if (p.Length != 3 || p[0] != "h" ||
                !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out e) ||
                !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return Bad("handle", "'" + handle + "' is not a handle");
            bool had = e == epoch && leases.Remove(id);
            return new { ok = true, released = had, held = leases.Count };
        }

        /// <summary>
        /// Named aliases, resolved FRESH on every request. Never cached: the tactical controller of
        /// two missions ago is a destroyed object, and an alias that still answered with it would be
        /// the most convincing wrong answer this endpoint could give.
        /// </summary>
        private static object Roots()
        {
            if (Protocol.RootsProbe == null) return Bad("args", "no roots probe installed");
            Dictionary<string, object> live = Protocol.RootsProbe();
            Dictionary<string, object> dto = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in live) dto[kv.Key] = Project(kv.Value);
            return new { ok = true, roots = dto };
        }

        /// <summary>
        /// A def's name and guid, read the same way for every caller. Returns false when the object
        /// refuses to answer either question - a def that throws on its own accessors is skipped, not
        /// reported as a nameless hit.
        /// </summary>
        private static bool DefIdentity(object def, Type dt, out string name, out string guid)
        {
            name = null;
            guid = null;
            PropertyInfo np = dt.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo gf = dt.GetField("Guid");
            try
            {
                if (np != null) name = (string)np.GetValue(def, null);
                if (gf != null && gf.FieldType == typeof(string)) guid = (string)gf.GetValue(def);
            }
            catch (Exception) { return false; }
            return true;
        }

        private static bool DefMatches(string name, string guid, string query)
        {
            return (guid != null && string.Equals(guid, query, StringComparison.OrdinalIgnoreCase)) ||
                   (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static object Find(JObject a)
        {
            if (a == null) return Bad("args", "find needs {query} (a def name substring or an exact guid), or {all:true} to enumerate");
            string query = (string)a["query"];

            // Enumeration is OPT-IN and strictly boolean. An empty or missing query on its own keeps
            // refusing exactly as it always did: a typo'd variable must never silently become a dump
            // of the whole def repository.
            JToken allTok = a["all"];
            bool all = allTok != null && allTok.Type == JTokenType.Boolean && (bool)allTok;
            if (!all && string.IsNullOrEmpty(query))
                return Bad("args", "find needs {query} (a def name substring or an exact guid); " +
                                   "pass {\"all\":true,\"page\":0,\"pageSize\":200} to enumerate the repository page by page");
            if (Protocol.AllDefs == null) return Bad("args", "no def repository installed");

            Type want = null;
            string typeName = (string)a["type"];
            if (!string.IsNullOrEmpty(typeName))
            {
                string error;
                want = ResolveType(typeName, (string)a["assembly"], out error);
                if (want == null) return Bad("type", error);
            }

            if (all) return FindAll(a, want, query);

            List<object> hits = new List<object>();
            bool more = false;
            foreach (object def in Protocol.AllDefs())
            {
                if (def == null) continue;
                Type dt = def.GetType();
                if (want != null && !want.IsInstanceOfType(def)) continue;

                string name, guid;
                if (!DefIdentity(def, dt, out name, out guid)) continue;
                if (!DefMatches(name, guid, query)) continue;
                if (hits.Count >= MaxFindResults) { more = true; break; }
                hits.Add(new { name, guid, type = dt.Name });
            }
            return new { ok = true, count = hits.Count, truncated = more, defs = hits.ToArray() };
        }

        /// <summary>
        /// Explicit, deterministic paging over the def repository - the one form that can build an
        /// on-disk index without re-deriving def names from source. A <paramref name="query"/> may
        /// still be given, in which case this is the same filter as <c>find</c> without its 100-hit
        /// ceiling.
        ///
        /// The sort is total and ordinal (name, then guid, then type) so a page boundary can neither
        /// skip nor duplicate a row - two defs really do share a name, and reflection order is not a
        /// promise. The 64 KB response cap in <see cref="Dispatch"/> is the real backstop: at the
        /// default 200 rows a page projects to roughly 30 KB, and an oversized page is refused by
        /// name rather than truncated into a lie.
        /// </summary>
        private static object FindAll(JObject a, Type want, string query)
        {
            int page = a["page"] == null ? 0 : (int)a["page"];
            int size = a["pageSize"] == null ? MaxPageSize : (int)a["pageSize"];
            if (page < 0) return Bad("args", "page must be >= 0");
            if (size < 1 || size > MaxPageSize) return Bad("args", "pageSize must be 1.." + MaxPageSize);

            List<string[]> rows = new List<string[]>();
            foreach (object def in Protocol.AllDefs())
            {
                if (def == null) continue;
                Type dt = def.GetType();
                if (want != null && !want.IsInstanceOfType(def)) continue;

                string name, guid;
                if (!DefIdentity(def, dt, out name, out guid)) continue;
                if (!string.IsNullOrEmpty(query) && !DefMatches(name, guid, query)) continue;
                rows.Add(new[] { name ?? "", guid ?? "", dt.Name });
            }

            rows.Sort(delegate(string[] x, string[] y)
            {
                int c = string.CompareOrdinal(x[0], y[0]);
                if (c != 0) return c;
                c = string.CompareOrdinal(x[1], y[1]);
                return c != 0 ? c : string.CompareOrdinal(x[2], y[2]);
            });

            // long, because page * pageSize is the one arithmetic here a caller can overflow.
            long skip = (long)page * size;
            if (skip > rows.Count) skip = rows.Count;
            List<object> defs = new List<object>();
            for (int i = (int)skip; i < rows.Count && defs.Count < size; i++)
                defs.Add(new { name = rows[i][0], guid = rows[i][1], type = rows[i][2] });

            return new
            {
                ok = true,
                count = defs.Count,
                total = rows.Count,
                page,
                pageSize = size,
                hasMore = skip + defs.Count < rows.Count,
                defs = defs.ToArray()
            };
        }
    }
}
