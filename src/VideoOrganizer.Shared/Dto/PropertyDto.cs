namespace VideoOrganizer.Shared.Dto;

public enum PropertyDataTypeDto
{
    Text,
    LongText,
    Number,
    Date,
    Boolean,
    Url
}

public enum PropertyScopeDto
{
    Tag,
    Video
}

// Wire shape for a PropertyDefinition. TagGroupId is required when
// Scope == Tag, must be null when Scope == Video.
public record PropertyDefinitionDto(
    Guid Id,
    string Name,
    PropertyDataTypeDto DataType,
    PropertyScopeDto Scope,
    Guid? TagGroupId,
    bool Required,
    int SortOrder,
    string Notes);

public record CreatePropertyDefinitionRequest(
    string Name,
    PropertyDataTypeDto DataType,
    PropertyScopeDto Scope,
    Guid? TagGroupId,
    bool Required = false,
    int SortOrder = 0,
    string Notes = "");

public record UpdatePropertyDefinitionRequest(
    string Name,
    PropertyDataTypeDto DataType,
    bool Required,
    int SortOrder,
    string Notes);

// One property value attached to a tag or video. Value is the raw string
// representation, parsed/formatted by the client based on DataType. Used
// both for reading (with Definition embedded so the UI can render) and for
// writing via /videos/{id}/properties or /tags/{id}/properties.
public record PropertyValueDto(
    Guid PropertyDefinitionId,
    string PropertyName,
    PropertyDataTypeDto DataType,
    string Value);

// PUT body — full replace of the value set on a video or tag.
public record SetPropertyValuesRequest(
    IReadOnlyList<PropertyValueWrite> Values);

public record PropertyValueWrite(
    Guid PropertyDefinitionId,
    string Value);
