namespace Content.Store.Models;

/// <summary>The few fields a name search returns, so a search never ships whole hotel documents.</summary>
public sealed record HotelSummary(long Id, string? Code, string? Name, string? CityCode, string? CountryCode, double Rating);
