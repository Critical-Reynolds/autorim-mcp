namespace AutoRim.Bridge
{
    /// <summary>
    /// Wire response shape. Every reply is either {"ok":true,"data":...} or
    /// {"ok":false,"error":{"code","message","hint","data"}}.
    /// </summary>
    public static class Envelope
    {
        public static JsonValue Ok(JsonValue data) =>
            JsonValue.NewObject()
                .Set("ok", true)
                .Set("data", data ?? JsonValue.Null);

        public static JsonValue Error(string code, string message, string hint = null, JsonValue data = null)
        {
            var error = JsonValue.NewObject()
                .Set("code", code)
                .Set("message", message ?? string.Empty);

            if (!string.IsNullOrEmpty(hint)) error.Set("hint", hint);
            if (data != null && !data.IsNull) error.Set("data", data);

            return JsonValue.NewObject()
                .Set("ok", false)
                .Set("error", error);
        }
    }
}
