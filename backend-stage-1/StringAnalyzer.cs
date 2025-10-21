public class StringAnalyzer
{
    public int StringLength(string input)
    {
        return input.Length;
    }

    public bool IsPalindrome(string input)
    {
        var reversed = new string(input.Reverse().ToArray());
        return string.Equals(input, reversed, StringComparison.OrdinalIgnoreCase);
    }

    public int UniqueCharacterCount(string input)
    {
        return input.ToLower().Where(char.IsLetterOrDigit).Distinct().Count();
    }

    public int WordCount(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return 0;

        var words = input.Split(new char[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Length;
    }

    public string ShaEncode(string input)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }
    }

    public Dictionary<char, int> GetCharacterFrequency(string input)
    {
        var freq = new Dictionary<char, int>();
        if (string.IsNullOrEmpty(input))
            return freq;

        foreach (var c in input)
        {
            if (freq.TryGetValue(c, out var count))
                freq[c] = count + 1;
            else
                freq[c] = 1;
        }

        return freq;
    }
}