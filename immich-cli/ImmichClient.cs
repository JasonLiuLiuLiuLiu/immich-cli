namespace OpenAPI
{
    public partial class ImmichClient
    {
        public string ApiKey { get; set; }

        partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, System.Text.StringBuilder urlBuilder)
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                throw new System.ArgumentNullException("token");
            }
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("x-api-key", ApiKey);
        }

    }
}
