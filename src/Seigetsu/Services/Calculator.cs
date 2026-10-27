using System.Globalization;
using System.Text;

namespace Seigetsu.Services;

/// <summary>
/// 電卓の式を計算する（v6 の safe_eval と同じく、数字と + - × ÷ ( ) % // ** だけ。式をプログラムとして実行はしない）。
/// 割り算の余り（%）と切り捨ての割り算（//）は、v6（Python）と同じく小さい方へ切り捨てる。
/// </summary>
public static class Calculator
{
    /// <summary>式の答え。式がおかしいときは FormatException、0 で割ったときは DivideByZeroException。</summary>
    public static double Evaluate(string expression)
    {
        // 全角の数字や記号も使えるようにする（１２＋３ → 12+3）
        // ^ もべき乗として使えるようにする（2^3 = 8）
        var text = expression.Normalize(NormalizationForm.FormKC)
            .Replace("×", "*").Replace("÷", "/").Replace("^", "**").Replace("−", "-");
        var parser = new Parser(text);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new FormatException("答えが大きすぎます");
        return value;
    }

    /// <summary>答えを画面に出す形にする（整数なら小数点なし。v6 と同じ）。</summary>
    public static string Format(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_pos < text.Length)
                throw new FormatException("式を確認してください");
        }

        // 足し算・引き算
        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                if (Take("+"))
                    value += ParseTerm();
                else if (Take("-"))
                    value -= ParseTerm();
                else
                    return value;
            }
        }

        // 掛け算・割り算・切り捨ての割り算・余り
        private double ParseTerm()
        {
            var value = ParseUnary();
            while (true)
            {
                if (Peek("**"))
                    return value; // ** は ParsePower で読む
                if (Take("*"))
                    value *= ParseUnary();
                else if (Take("//"))
                    value = Math.Floor(value / NonZero(ParseUnary()));
                else if (Take("/"))
                    value /= NonZero(ParseUnary());
                else if (Take("%"))
                {
                    var right = NonZero(ParseUnary());
                    value -= right * Math.Floor(value / right);
                }
                else
                    return value;
            }
        }

        // 先頭の + -（Python と同じく、-2**2 は -(2**2)）
        private double ParseUnary()
        {
            if (Take("-"))
                return -ParseUnary();
            if (Take("+"))
                return ParseUnary();
            return ParsePower();
        }

        // べき乗（右から計算する。v6 と同じく、100 乗より大きいものは計算しない）
        private double ParsePower()
        {
            var value = ParseAtom();
            if (!Take("**"))
                return value;
            var exponent = ParseUnary();
            if (Math.Abs(exponent) > 100)
                throw new FormatException("大きすぎるべき乗です");
            return Math.Pow(value, exponent);
        }

        private double ParseAtom()
        {
            if (Take("("))
            {
                var value = ParseExpression();
                if (!Take(")"))
                    throw new FormatException("かっこが閉じていません");
                return value;
            }
            SkipSpaces();
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.'))
                _pos++;
            if (_pos == start || !double.TryParse(text.AsSpan(start, _pos - start), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var number))
                throw new FormatException("式を確認してください");
            return number;
        }

        private static double NonZero(double value) => value == 0 ? throw new DivideByZeroException() : value;

        private bool Peek(string token)
        {
            SkipSpaces();
            return string.CompareOrdinal(text, _pos, token, 0, token.Length) == 0;
        }

        private bool Take(string token)
        {
            if (!Peek(token))
                return false;
            _pos += token.Length;
            return true;
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }
    }
}
