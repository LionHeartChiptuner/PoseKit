using System;
using System.Collections.Generic;
using System.Text;

namespace GT3PoseKit
{
    // How alike two gun names are, 0..1. The editor has an identical copy of this rule (nameSimilarity)
    // so "Find similar guns" and in-game auto copy always agree.
    //
    // Names are split into letter and number tokens: "G17 Gen3" -> g, 17, gen, 3.
    //   * The first letter token must match ("Glock17" never matches "G17"; compare the display names for that).
    //   * Same tokens except numbers within 2 of each other -> 0.9 (G17 ~ G18 ~ G19, but not M1911 ~ M1903).
    //   * One name is the other plus extra tokens -> 0.85 (G17 ~ G17L, M4 ~ M4A1).
    //   * Otherwise an edit-distance ratio, which only passes for long, nearly identical names.
    public static class Similarity
    {
        public const float DefaultThreshold = 0.8f;

        public static float Score(string a, string b)
        {
            var na = Normalize(a);
            var nb = Normalize(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            if (na == nb) return 1;
            var ta = Tokens(na);
            var tb = Tokens(nb);
            if (ta.Count == 0 || tb.Count == 0) return 0;
            if (FirstAlpha(ta) != FirstAlpha(tb)) return 0;

            if (ta.Count == tb.Count)
            {
                bool allClose = true, anyDiff = false;
                for (int i = 0; i < ta.Count; i++)
                {
                    if (ta[i] == tb[i]) continue;
                    anyDiff = true;
                    int x, y;
                    bool nx = int.TryParse(ta[i], out x), ny = int.TryParse(tb[i], out y);
                    if (!(nx && ny && Math.Abs(x - y) <= 2)) { allClose = false; break; }
                }
                if (anyDiff && allClose) return 0.9f;
            }
            var shorter = ta.Count < tb.Count ? ta : tb;
            var longer = ta.Count < tb.Count ? tb : ta;
            if (shorter.Count != longer.Count && shorter.Count >= 2)
            {
                bool prefix = true;
                for (int i = 0; i < shorter.Count; i++) if (shorter[i] != longer[i]) { prefix = false; break; }
                if (prefix) return 0.85f;
            }
            int d = Levenshtein(na, nb);
            return 1f - (float)d / Math.Max(na.Length, nb.Length);
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        static List<string> Tokens(string s)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            int kind = -1;
            foreach (var c in s)
            {
                int k = char.IsDigit(c) ? 1 : 0;
                if (k != kind && cur.Length > 0) { list.Add(cur.ToString()); cur.Length = 0; }
                kind = k;
                cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            // Drop leading zeros so "G017" and "G17" compare equal.
            for (int i = 0; i < list.Count; i++)
                if (char.IsDigit(list[i][0])) { var t = list[i].TrimStart('0'); list[i] = t.Length == 0 ? "0" : t; }
            return list;
        }

        static string FirstAlpha(List<string> t)
        {
            foreach (var x in t) if (!char.IsDigit(x[0])) return x;
            return "";
        }

        static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
