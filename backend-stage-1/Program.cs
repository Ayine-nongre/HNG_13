var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<StringAnalyzer>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

List<StringDetail> data = new();

app.MapPost("/strings", (StringAnalyzer analyzer, StringRequest request) =>
{
    if (request == null || string.IsNullOrEmpty(request.Value))
    {
        return Results.BadRequest("Invalid request body or missing value field");
    }

    if (data.Any(d => d.Value == request.Value))
    {
        return Results.Conflict("String already exists in the system");
    }

    if (request.Value.GetType() != typeof(string))
    {
        return Results.BadRequest("nvalid data type for value (must be string)");
    }

    var input = request.Value;
    var result = new
    {
        Length = analyzer.StringLength(input),
        Is_Palindrome = analyzer.IsPalindrome(input),
        Unique_Characters = analyzer.UniqueCharacterCount(input),
        Word_Count = analyzer.WordCount(input),
        Sha256_hash = analyzer.ShaEncode(input),
        Character_frequency_map = analyzer.GetCharacterFrequency(input)
    };

    StringDetail detail = new StringDetail
    {
        Id = analyzer.ShaEncode(input),
        Value = input,
        Properties = result,
        Created_At = DateTime.UtcNow
    };
    data.Add(detail);

    return Results.Created(detail.Id, detail);
});

app.MapGet("/strings/{string_value}", (string string_value) =>
{
    var detail = data.FirstOrDefault(d => d.Value == string_value);
    if (detail == null)
    {
        return Results.NotFound("String does not exist in the system");
    }
    return Results.Ok(detail);
});

app.MapGet("/strings", (bool? is_palindrome = false, int? min_length = null,
    int? max_length = null, int? word_count = null, string? contains_character = null) => {
    
    if (min_length?.GetType() != typeof(int?) ||
        max_length?.GetType() != typeof(int?) ||
        word_count?.GetType() != typeof(int?) ||
        is_palindrome?.GetType() != typeof(bool?) ||
        (contains_character != null && contains_character?.GetType() != typeof(string)))
    {
        return Results.BadRequest("Invalid query parameter types");
    }
    
    var query = data.AsEnumerable();
    if (is_palindrome.HasValue)
    {
        query = query.Where(d => ((dynamic)d.Properties).Is_Palindrome == is_palindrome.Value);
    }
    if (min_length.HasValue)
    {
        query = query.Where(d => ((dynamic)d.Properties).Length >= min_length.Value);
    }
    if (max_length.HasValue)
    {
        query = query.Where(d => ((dynamic)d.Properties).Length <= max_length.Value);
    }
    if (word_count.HasValue)
    {
        query = query.Where(d => ((dynamic)d.Properties).Word_Count == word_count.Value);
    }
    if (!string.IsNullOrEmpty(contains_character))
    {
        query = query.Where(d => ((dynamic)d.Properties).Character_frequency_map.ContainsKey(contains_character[0]));
    }
    return Results.Ok(query.ToList());
});

app.MapGet("/strings/filter-by-natural-language", (string query, StringAnalyzer analyzer) =>
{
    try
    {
        // Step 1: Parse the query into structured filters
        var filters = NaturalLanguageParser.Parse(query);
        if (filters is null)
            return Results.BadRequest(new { error = "Unable to parse natural language query" }); // 400

        // Step 2: Detect conflicting filters (e.g., both palindrome=true and palindrome=false)
        if (filters.HasConflicts())
            return Results.UnprocessableEntity(new { error = "Query parsed but resulted in conflicting filters" }); // 422

        // Step 3: Apply filters to the stored strings
        var filtered = data.AsEnumerable();

        if (filters.IsPalindrome.HasValue)
            filtered = filtered.Where(d => analyzer.IsPalindrome(d.Value) == filters.IsPalindrome.Value);

        if (filters.WordCount.HasValue)
            filtered = filtered.Where(d => analyzer.WordCount(d.Value) == filters.WordCount.Value);

        if (filters.MinLength.HasValue)
            filtered = filtered.Where(d => analyzer.StringLength(d.Value) >= filters.MinLength.Value);

        if (filters.MaxLength.HasValue)
            filtered = filtered.Where(d => analyzer.StringLength(d.Value) <= filters.MaxLength.Value);

        if (!string.IsNullOrEmpty(filters.ContainsCharacter))
            filtered = filtered.Where(d => d.Value.Contains(filters.ContainsCharacter, StringComparison.OrdinalIgnoreCase));

        var result = filtered.ToList();

        // Step 4: Return structured response
        return Results.Ok(new
        {
            data = result,
            count = result.Count,
            interpreted_query = new
            {
                original = query,
                parsed_filters = filters
            }
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/strings/{string_value}", (string string_value) =>
{
    var detail = data.FirstOrDefault(d => d.Value == string_value);
    if (detail == null)
    {
        return Results.NotFound("String does not exist in the system");
    }
    data.Remove(detail);
    return Results.NoContent();
});

app.Run();

public class StringDetail
{
    public string Id { get; set; }
    public string Value { get; set; }
    public object Properties { get; set; }
    public DateTime Created_At { get; set; }
}

public record StringRequest(string Value);

public class FilterCriteria
{
    public bool? IsPalindrome { get; set; }
    public int? WordCount { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? ContainsCharacter { get; set; }

    // Optional helper to detect conflicts
    public bool HasConflicts()
    {
        // Example: impossible logic like MinLength > MaxLength or conflicting IsPalindrome flags
        if (MinLength.HasValue && MaxLength.HasValue && MinLength > MaxLength)
            return true;

        return false;
    }
}

public static class NaturalLanguageParser
{
    public static FilterCriteria? Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var filters = new FilterCriteria();
        query = query.ToLower();

        // Palindrome detection
        if (query.Contains("palindrome") || query.Contains("palindromic"))
            filters.IsPalindrome = true;
        if (query.Contains("not palindromic") || query.Contains("non-palindromic"))
            filters.IsPalindrome = false;

        // Word count inference
        if (query.Contains("single word") || query.Contains("one word"))
            filters.WordCount = 1;
        else if (query.Contains("two words"))
            filters.WordCount = 2;
        else if (query.Contains("three words"))
            filters.WordCount = 3;

        // Length conditions
        var lengthMatch = System.Text.RegularExpressions.Regex.Match(query, @"longer than (\d+)");
        if (lengthMatch.Success)
            filters.MinLength = int.Parse(lengthMatch.Groups[1].Value) + 1;

        var shorterMatch = System.Text.RegularExpressions.Regex.Match(query, @"shorter than (\d+)");
        if (shorterMatch.Success)
            filters.MaxLength = int.Parse(shorterMatch.Groups[1].Value) - 1;

        // Character containment
        var charMatch = System.Text.RegularExpressions.Regex.Match(query, @"containing the letter (\w)");
        if (charMatch.Success)
            filters.ContainsCharacter = charMatch.Groups[1].Value;

        // Heuristic: "contains the first vowel"
        if (query.Contains("first vowel"))
            filters.ContainsCharacter = "a"; // default heuristic

        // Return null if no keywords matched
        if (filters.IsPalindrome == null && filters.WordCount == null &&
            filters.MinLength == null && filters.MaxLength == null &&
            string.IsNullOrEmpty(filters.ContainsCharacter))
            return null;

        return filters;
    }
}