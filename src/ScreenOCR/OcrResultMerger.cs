namespace ScreenOCR;

/// <summary>
/// マージのしきい値。既定値は実測（SPEC §1.6/§1.7/§1.8）に基づき調整済み。<see cref="AppConfig"/> から渡す。
///
/// <see cref="MarginScore"/> は M-2 対応でスコアが文字ごとの重み付け（小数）になったことに伴い
/// double 化した。重み付けスコアは同程度の分量のテキストなら 1 未満の差になることが多いため、
/// 旧スコア（生の文字数、差が「2」程度でも十分大きかった）と同じ感覚の margin は大きすぎる。
/// 実測（"e 四代-2026. x (" 相当のグループ vs "repott_2026.xlsx (" 相当の候補、差は約 0.4）に基づき
/// 既定値を 0.3 とした。
///
/// <see cref="TieBreakMarginScore"/> / <see cref="TieBreakCjkRatio"/> は §1.8 の取りこぼし修正（B）用。
/// スコア差が <see cref="MarginScore"/> に届かず（＝通常ルールでは置換されず）、かつスコア差の絶対値が
/// <see cref="TieBreakMarginScore"/> 未満の「実質同点」領域に限り、ベースラン文字列の CJK 比率が
/// <see cref="TieBreakCjkRatio"/> 未満（＝ラテン文字・数字主体）ならセカンダリ候補を採用する。
/// CJK 比率がこれ以上（＝CJK 主体）ならベースを維持する。実測（"Windows11"→"Windows 11"、
/// "Windows 1 1 Education"→"Windows 11 Education" はいずれもスコア同点だった）に基づき既定値は
/// <see cref="MarginScore"/> と同じ 0.3、CJK 比率閾値は 0.3 とした。
/// </summary>
public readonly record struct OcrMergeOptions(
    OcrScoreWeights Weights, double MarginScore, double BaseCjkRatio, double CoverageAreaRatio,
    double TieBreakMarginScore, double TieBreakCjkRatio)
{
    public static OcrMergeOptions Default => new(OcrScoreWeights.Default, 0.3, 0.30, 0.5, 0.3, 0.3);
}

/// <summary>
/// 1 つのグループ（候補語が跨るベースランをまとめた単位）に対するマージ判定の記録。
/// <paramref name="TieBreak"/> は §1.8 修正 B（同点/僅差時の字種による判定）が実際に採否を
/// 決めたかどうか。通常のマージン判定（<see cref="OcrMergeOptions.MarginScore"/>）だけで
/// 決着した場合は false。
/// </summary>
public sealed record OcrRunDecision(
    int LineIndex, int RunIndex, string BaseText, string? CandidateText,
    double BaseScore, double? CandidateScore, bool Replaced, bool TieBreak = false);

/// <summary>マージ処理全体のサマリー。<see cref="OcrPipelineResult.MergeInfo"/> にそのまま入る。</summary>
public sealed record OcrMergeSummary(
    bool Attempted, bool Merged, string? Reason,
    string BaseLanguage, string? SecondaryLanguage,
    double BaseCjkRatio, double SecondaryCjkRatio,
    int RunCount, int ReplacedRunCount,
    IReadOnlyList<OcrRunDecision> Decisions)
{
    public static OcrMergeSummary NotAttempted(string reason, string baseLanguage) =>
        new(false, false, reason, baseLanguage, null, 0, 0, 0, 0, []);
}

/// <summary>
/// 2 つの OCR エンジンの結果を統合する純粋関数。WinRT 型に依存せず、<see cref="OcrLineInfo"/> /
/// <see cref="OcrWordInfo"/> / <see cref="RectF"/> の DTO のみを扱うため、OCR エンジンなしで単体テストできる。
/// </summary>
public static class OcrResultMerger
{
    /// <summary>
    /// <paramref name="primaryLines"/> を軸に、<paramref name="secondaryLines"/> の単語で
    /// 品質の高いランを置き換える。両方とも同じ前処理済みビットマップに対する認識結果である前提
    /// （＝バウンディングボックスが同一座標系）。
    ///
    /// ベースランは「同じ候補語に実質的に覆われているランの集合」でグループ化してから採否判定する
    /// （M-1 対応）。これにより、1 つの候補語（例: "repott_2026.xlsx"）が複数の狭いベースラン
    /// （"e" / "四代-2026." / "x ("）に跨って重なる場合でも、グループ全体を候補語でまとめて置換でき、
    /// 置換後に前後のランの残骸（"erepott_2026.xlsxx" のような文字化け）が残らない。
    /// </summary>
    public static (IReadOnlyList<OcrLineInfo> Lines, OcrMergeSummary Summary) Merge(
        IReadOnlyList<OcrLineInfo> primaryLines, string primaryLanguage,
        IReadOnlyList<OcrLineInfo> secondaryLines, string secondaryLanguage,
        OcrMergeOptions options)
    {
        double primaryRatio = CjkRatio(primaryLines);
        double secondaryRatio = CjkRatio(secondaryLines);
        bool baseIsPrimary = primaryRatio >= options.BaseCjkRatio;

        IReadOnlyList<OcrLineInfo> baseLines = baseIsPrimary ? primaryLines : secondaryLines;
        IReadOnlyList<OcrLineInfo> otherLines = baseIsPrimary ? secondaryLines : primaryLines;
        string baseLanguage = baseIsPrimary ? primaryLanguage : secondaryLanguage;
        string otherLanguage = baseIsPrimary ? secondaryLanguage : primaryLanguage;

        List<OcrWordInfo> otherWords = otherLines.SelectMany(x => x.Words).ToList();

        var mergedLines = new List<OcrLineInfo>(baseLines.Count);
        var decisions = new List<OcrRunDecision>();

        for (int lineIndex = 0; lineIndex < baseLines.Count; lineIndex++)
        {
            IReadOnlyList<Run> runs = SplitRuns(baseLines[lineIndex].Words);
            IReadOnlyList<RunGroup> groups = GroupRunsByCandidateCoverage(runs, otherWords, options.CoverageAreaRatio);

            var mergedWords = new List<OcrWordInfo>();
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                RunGroup group = groups[groupIndex];
                string baseText = JoinWords(group.BaseWords);

                if (group.CandidateWords.Count == 0)
                {
                    mergedWords.AddRange(group.BaseWords);
                    decisions.Add(new OcrRunDecision(lineIndex, groupIndex, baseText, null,
                        OcrQualityScorer.EvaluateFragment(baseText, options.Weights).Score, null, false));
                    continue;
                }

                string candidateText = JoinWords(group.CandidateWords);
                double baseScore = OcrQualityScorer.EvaluateFragment(baseText, options.Weights).Score;
                double candidateScore = OcrQualityScorer.EvaluateFragment(candidateText, options.Weights).Score;
                double diff = candidateScore - baseScore;

                bool replace;
                bool tieBreak = false;
                if (diff >= options.MarginScore)
                {
                    replace = true;
                }
                else if (Math.Abs(diff) < options.TieBreakMarginScore)
                {
                    // 修正 B（§1.8）: 通常判定では決着しない実質同点。ベースラン自体の字種で決める。
                    // ラテン文字・数字主体（CJK 比率が閾値未満）ならセカンダリを、CJK 主体ならベースを採る。
                    replace = CjkRatioOfText(baseText) < options.TieBreakCjkRatio;
                    tieBreak = true;
                }
                else
                {
                    replace = false;
                }

                mergedWords.AddRange(replace ? group.CandidateWords : group.BaseWords);
                decisions.Add(new OcrRunDecision(lineIndex, groupIndex, baseText, candidateText, baseScore, candidateScore, replace, tieBreak));
            }
            mergedLines.Add(new OcrLineInfo(mergedWords));
        }

        var summary = new OcrMergeSummary(
            Attempted: true, Merged: true, Reason: null,
            BaseLanguage: baseLanguage, SecondaryLanguage: otherLanguage,
            BaseCjkRatio: baseIsPrimary ? primaryRatio : secondaryRatio,
            SecondaryCjkRatio: baseIsPrimary ? secondaryRatio : primaryRatio,
            RunCount: decisions.Count,
            ReplacedRunCount: decisions.Count(x => x.Replaced),
            Decisions: decisions);

        return (mergedLines, summary);
    }

    private static double CjkRatio(IReadOnlyList<OcrLineInfo> lines)
    {
        int total = 0, cjk = 0;
        foreach (OcrLineInfo line in lines)
        {
            foreach (OcrWordInfo word in line.Words)
            {
                foreach (char c in word.Text)
                {
                    if (char.IsWhiteSpace(c)) continue;
                    total++;
                    if (TextPostProcessor.IsCjk(c)) cjk++;
                }
            }
        }
        return total == 0 ? 0.0 : (double)cjk / total;
    }

    /// <summary>修正 B 用: 1 文字列中の CJK 文字比率（空白除く）。空文字列や空白のみなら 0。</summary>
    private static double CjkRatioOfText(string text)
    {
        int total = 0, cjk = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            total++;
            if (TextPostProcessor.IsCjk(c)) cjk++;
        }
        return total == 0 ? 0.0 : (double)cjk / total;
    }

    private static string JoinWords(IReadOnlyList<OcrWordInfo> words) => string.Join(" ", words.Select(x => x.Text));

    private sealed record RunGroup(IReadOnlyList<OcrWordInfo> BaseWords, IReadOnlyList<OcrWordInfo> CandidateWords);

    /// <summary>
    /// ベースランを、同じ候補語（他エンジンの単語）に実質的に覆われているもの同士でグループ化する。
    /// 候補語 1 個が複数のベースランに重なる場合、Union-Find でそれらのランを 1 グループにまとめる。
    /// グループの候補語は、グループ内のいずれかのランに重なった候補語すべてを X 座標順に連結したもの。
    /// </summary>
    private static IReadOnlyList<RunGroup> GroupRunsByCandidateCoverage(
        IReadOnlyList<Run> runs, IReadOnlyList<OcrWordInfo> otherWords, double coverageAreaRatio)
    {
        if (runs.Count == 0) return [];

        var parent = new int[runs.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        var runCandidates = new List<OcrWordInfo>[runs.Count];
        for (int i = 0; i < runs.Count; i++) runCandidates[i] = [];

        foreach (OcrWordInfo candidate in otherWords)
        {
            List<int> matches = [];
            for (int i = 0; i < runs.Count; i++)
            {
                if (SubstantiallyOverlaps(runs[i].Bounds, candidate.Bounds, coverageAreaRatio)) matches.Add(i);
            }
            if (matches.Count == 0) continue;
            foreach (int idx in matches) runCandidates[idx].Add(candidate);
            for (int k = 1; k < matches.Count; k++) Union(matches[0], matches[k]);
        }

        var groupsByRoot = new Dictionary<int, List<int>>();
        for (int i = 0; i < runs.Count; i++)
        {
            int root = Find(i);
            if (!groupsByRoot.TryGetValue(root, out List<int>? list)) groupsByRoot[root] = list = [];
            list.Add(i);
        }

        return groupsByRoot.Values
            .OrderBy(indices => indices[0])
            .Select(indices =>
            {
                indices.Sort();
                List<OcrWordInfo> baseWords = indices.SelectMany(i => runs[i].Words).ToList();
                List<OcrWordInfo> candidateWords = indices.SelectMany(i => runCandidates[i])
                    .Distinct()
                    .OrderBy(word => word.Bounds.Left)
                    .ToList();
                return new RunGroup(baseWords, candidateWords);
            })
            .ToList();
    }

    /// <summary>
    /// ベースラン矩形 <paramref name="runBounds"/> と候補語矩形 <paramref name="candidateBounds"/> が
    /// 「実質的に重なる」かどうか。(1) どちらかの中心点がもう一方の矩形内にある、または
    /// (2) 交差面積 / 小さい方の面積 が <paramref name="areaRatioThreshold"/> 以上、のいずれかで真。
    /// (2) により、1 つの広い候補語が複数の狭いベースランに跨って重なるケース（M-1）を検出できる
    /// （狭いランは自身の面積のほとんどが候補語と重なるため、面積比が高くなる）。
    /// </summary>
    private static bool SubstantiallyOverlaps(RectF runBounds, RectF candidateBounds, double areaRatioThreshold)
    {
        if (CenterInside(candidateBounds, runBounds) || CenterInside(runBounds, candidateBounds)) return true;

        double runArea = (double)Math.Max(runBounds.Width, 0) * Math.Max(runBounds.Height, 0);
        double candidateArea = (double)Math.Max(candidateBounds.Width, 0) * Math.Max(candidateBounds.Height, 0);
        if (runArea <= 0 || candidateArea <= 0) return false;

        double interWidth = Math.Max(0, Math.Min(runBounds.Right, candidateBounds.Right) - Math.Max(runBounds.Left, candidateBounds.Left));
        double interHeight = Math.Max(0, Math.Min(runBounds.Bottom, candidateBounds.Bottom) - Math.Max(runBounds.Top, candidateBounds.Top));
        double interArea = interWidth * interHeight;

        double minArea = Math.Min(runArea, candidateArea);
        return interArea / minArea >= areaRatioThreshold;
    }

    private static bool CenterInside(RectF inner, RectF outer)
    {
        float cx = inner.X + inner.Width / 2f;
        float cy = inner.Y + inner.Height / 2f;
        return cx >= outer.Left && cx <= outer.Right && cy >= outer.Top && cy <= outer.Bottom;
    }

    private enum ScriptClass { Cjk, Latin, Mixed, Other }

    /// <summary>
    /// 修正 A（§1.8）: 数字はラテン文字と同じ「非 CJK」バケットに分類する。これにより数字のみの単語
    /// （例: "2026"）は Other として直前の CJK ランへ付随せず、独立したラン境界を作れる。
    /// これがないと「ビルド」+「2628.9106」のような数字が CJK の得点に紛れ込み、数字だけを
    /// セカンダリ候補で正しく置換する機会を失う。
    /// </summary>
    private static bool IsLatinLetterOrDigit(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static ScriptClass Classify(string text)
    {
        bool hasCjk = text.Any(TextPostProcessor.IsCjk);
        bool hasLatin = text.Any(IsLatinLetterOrDigit);
        if (hasCjk && hasLatin) return ScriptClass.Mixed;
        if (hasCjk) return ScriptClass.Cjk;
        if (hasLatin) return ScriptClass.Latin;
        return ScriptClass.Other;
    }

    /// <summary>
    /// 修正 A（§1.8）: 1 つの OCR 単語（WinRT が返す最小単位）が CJK とラテン文字・数字を内部で
    /// 混在させている場合（例: "四代-2026." や "ビルド2628.9106"）、その境界でさらに分割する。
    /// バウンディングボックスは文字数の比率で単語矩形を按分した近似値（実測値ではない）。
    /// 分割不要（単一スクリプトのみ）な場合は元の単語をそのまま返す。
    /// 句読点・記号などの中立文字は隣接する非中立文字の側（前方優先、無ければ後方）へ付随させる。
    /// </summary>
    private static IReadOnlyList<OcrWordInfo> SplitWordByScript(OcrWordInfo word)
    {
        string text = word.Text;
        if (text.Length == 0) return [word];

        var rawClass = new ScriptClass?[text.Length];
        bool hasCjk = false, hasNonCjk = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (TextPostProcessor.IsCjk(c)) { rawClass[i] = ScriptClass.Cjk; hasCjk = true; }
            else if (IsLatinLetterOrDigit(c)) { rawClass[i] = ScriptClass.Latin; hasNonCjk = true; }
        }
        if (!hasCjk || !hasNonCjk) return [word]; // CJK/非CJKどちらかしか無い＝分割不要

        ScriptClass? prev = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (rawClass[i] is null) rawClass[i] = prev; else prev = rawClass[i];
        }
        ScriptClass? next = null;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (rawClass[i] is null) rawClass[i] = next; else next = rawClass[i];
        }

        var fragments = new List<OcrWordInfo>();
        int segStart = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || rawClass[i] != rawClass[segStart])
            {
                fragments.Add(MakeWordFragment(word, text, segStart, i));
                segStart = i;
            }
        }
        return fragments;
    }

    /// <summary>単語矩形を文字数比率で按分した近似矩形を持つ部分単語を作る。実測ではなく推定値。</summary>
    private static OcrWordInfo MakeWordFragment(OcrWordInfo word, string text, int start, int end)
    {
        if (start == 0 && end == text.Length) return word;
        double startFraction = (double)start / text.Length;
        double endFraction = (double)end / text.Length;
        float left = word.Bounds.Left + (float)(startFraction * word.Bounds.Width);
        float right = word.Bounds.Left + (float)(endFraction * word.Bounds.Width);
        var bounds = new RectF(left, word.Bounds.Top, right - left, word.Bounds.Height);
        return new OcrWordInfo(text[start..end], bounds);
    }

    /// <summary>
    /// 単語列を、スクリプト種（CJK連続 / ラテン連続 / 混在）で極大な連続区間に切る。
    /// 数字・記号のみの単語（Other）は直前のランへ付随させる。混在（Mixed）単語は常に単独のランになる。
    /// まず修正 A（<see cref="SplitWordByScript"/>）で各単語を CJK / ラテン・数字の境界で分割してから
    /// ラン組み立てを行うため、1 単語内で CJK と数字・ラテン文字が同居していても正しく分かれる。
    /// </summary>
    private static IReadOnlyList<Run> SplitRuns(IReadOnlyList<OcrWordInfo> words)
    {
        List<OcrWordInfo> fragments = words.SelectMany(SplitWordByScript).ToList();

        var runs = new List<Run>();
        var current = new List<OcrWordInfo>();
        ScriptClass? currentClass = null;

        void Flush()
        {
            if (current.Count == 0) return;
            runs.Add(new Run(current.ToList(), UnionBounds(current)));
            current.Clear();
        }

        foreach (OcrWordInfo word in fragments)
        {
            ScriptClass cls = Classify(word.Text);
            if (cls == ScriptClass.Mixed)
            {
                Flush();
                currentClass = null;
                runs.Add(new Run([word], word.Bounds));
                continue;
            }

            ScriptClass effective = cls == ScriptClass.Other && currentClass is not null ? currentClass.Value : cls;
            if (currentClass is null || currentClass == effective)
            {
                current.Add(word);
                currentClass = effective;
            }
            else
            {
                Flush();
                current.Add(word);
                currentClass = effective;
            }
        }
        Flush();
        return runs;
    }

    private static RectF UnionBounds(IReadOnlyList<OcrWordInfo> words)
    {
        float left = words.Min(x => x.Bounds.Left);
        float top = words.Min(x => x.Bounds.Top);
        float right = words.Max(x => x.Bounds.Right);
        float bottom = words.Max(x => x.Bounds.Bottom);
        return new RectF(left, top, right - left, bottom - top);
    }

    private sealed record Run(IReadOnlyList<OcrWordInfo> Words, RectF Bounds);
}
