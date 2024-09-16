using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public class RequestEnvelope
{
    public string WebRequestType { get; set; }
    public string WebRequestArgs { get; set; }
    public CommonArguments CommonArguments { get; set; }
    public Args Args { get; set; }
    public bool CancellationTokenCancelled { get; set; }
    public string Headers { get; set; }
}

public class CommonArguments
{
    public URL URL { get; set; }
    public int AttemptsCount { get; set; }
    public int Timeout { get; set; }
    public object CustomDownloadHandler { get; set; }
}

public class URL
{
    public string Value { get; set; }
}

public class Args
{
    public object MultipartFormSections { get; set; }
    public WWWForm WWWForm { get; set; }
    public string PostData { get; set; }
    public string PutData { get; set; }
    public AudioType AudioType { get; set; }

    public string ContentType { get; set; }
}

public class WebRequestExecutioner : MonoBehaviour
{
    public TextAsset recordedWebRequests;
    public int timesToRepeat = 1;
    
    private int maxConcurrentRequests = 20; // Maximum number of concurrent requests
    private SemaphoreSlim semaphore;
    private List<byte[]> memoryChunks;
    private CancellationTokenSource cts;

    private int totalRequestsCompleted;
    private int totalRequestsFailed;
    private int totalRequestsFinishedWithExceptions;


    private void Start()
    {
        var requestEnvelopes = BuildRequestEnvelopeList();

        AllocateMemory(20);
        CountWebRequests(requestEnvelopes);
        StartRequests(requestEnvelopes);
    }

    private async void StartRequests(List<RequestEnvelope> requestEnvelopes)
    {
        for (int i = 0; i < timesToRepeat; i++)
            await ProcessRequests(requestEnvelopes);
        
        Debug.Log($"TOTAL REQUESTS COMPLETED {totalRequestsCompleted}");
        Debug.Log($"TOTAL REQUESTS FAILED {totalRequestsFailed}");
        Debug.Log($"TOTAL REQUESTS WITH EXCEPTIONS {totalRequestsFinishedWithExceptions}");

    }

    // Method to allocate a specified amount of memory (in GB)
    public void AllocateMemory(float gigabytes)
    {
        int chunkSize = 100 * 1024 * 1024; // 100 MB per chunk
        int totalChunks = (int)(gigabytes * 1024 / 100); // gigabytes / 100 MB per chunk
        memoryChunks = new List<byte[]>();
        for (int i = 0; i < totalChunks; i++)
        {
            // Allocate 100 MB chunks and add to list
            memoryChunks.Add(new byte[chunkSize]);
        }

        Debug.Log($"Memory allocation complete. {gigabytes} GB allocated.");
    }

    private void OnDestroy()
    {
        cts.Cancel();
    }
    
    // Method to process all requests asynchronously
    private async UniTask ProcessRequests(List<RequestEnvelope> requestEnvelopes)
    {
        semaphore = new SemaphoreSlim(maxConcurrentRequests);
        cts = new CancellationTokenSource();
        
        List<UniTask> tasks = new List<UniTask>();
        

        foreach (var requestEnvelope in requestEnvelopes)
        {
            // Add each request handling task to the list, and handle the concurrency limit
            tasks.Add(HandleRequest(requestEnvelope, cts.Token));
        }

        // Wait for all the tasks to complete
        await UniTask.WhenAll(tasks);
    }

    // Method to handle the actual UnityWebRequest based on the request type
    private async UniTask HandleRequest(RequestEnvelope requestEnvelope, CancellationToken ct)
    {
        // Wait for the semaphore to allow this request to execute (respect concurrency limit)
        await semaphore.WaitAsync(ct);

        if (ct.IsCancellationRequested) return;

        try
        {
            UnityWebRequest request = null;

            switch (requestEnvelope.WebRequestType)
            {
                case "GenericGetRequest":
                    request = UnityWebRequest.Get(requestEnvelope.CommonArguments.URL.Value);
                    break;
                case "GenericHeadRequest":
                    request = UnityWebRequest.Head(requestEnvelope.CommonArguments.URL.Value);
                    break;
                case "GenericPostRequest":
                    request = UnityWebRequest.Post(requestEnvelope.CommonArguments.URL.Value, requestEnvelope.Args.PostData, requestEnvelope.Args.ContentType);
                    break;
                case "GenericPutRequest":
                    request = UnityWebRequest.Post(requestEnvelope.CommonArguments.URL.Value, requestEnvelope.Args.PutData, requestEnvelope.Args.ContentType);
                    request.method = "PUT";
                    break;
                case "GetTextureWebRequest":
                    request = UnityWebRequestTexture.GetTexture(requestEnvelope.CommonArguments.URL.Value);
                    break;
                case "GetAudioClipWebRequest":
                    request = UnityWebRequestMultimedia.GetAudioClip(requestEnvelope.CommonArguments.URL.Value, requestEnvelope.Args.AudioType);
                    break;
                case "GetAssetBundleWebRequest":
                    request = UnityWebRequestAssetBundle.GetAssetBundle(requestEnvelope.CommonArguments.URL.Value);
                    break;
                // Add more cases for other WebRequestTypes here...
                default:
                    Debug.Log($"Unknown WebRequestType: {requestEnvelope.WebRequestType}");
                    break;
            }

            if (request != null)
            {
                try
                {
                    // Send the request and await its completion
                    var response = await request.SendWebRequest();
                    if (response.result == UnityWebRequest.Result.Success)
                    {
                        Debug.Log($"Request completed successfully for {requestEnvelope.WebRequestType}");
                        totalRequestsCompleted++;
                    }
                    else
                    {
                        Debug.LogError($"Request failed for {requestEnvelope.WebRequestType}: {response.error}");
                        totalRequestsFailed++;
                    }
                }
                catch (UnityWebRequestException exception)
                {
                    Debug.LogError($"Request threw exception for {requestEnvelope.WebRequestType}: {exception.Message}");
                    totalRequestsFinishedWithExceptions++;
                }
            }
        }
        finally
        {
            // Always release the semaphore, even if an exception occurs
            semaphore.Release();
        }
    }

    private List<RequestEnvelope> BuildRequestEnvelopeList()
    {
        var requestEnvelopes = new List<RequestEnvelope>();

        // Read the JSON file
        var jsonString = recordedWebRequests.text;
        var jsonStringList = JsonConvert.DeserializeObject<List<string>>(jsonString);
        foreach (var serializedRequest in jsonStringList)
        {
            var jsonObject = JsonConvert.DeserializeObject<JObject>(serializedRequest);
            var requestEnvelope = jsonObject["RequestEnvelope"].ToObject<RequestEnvelope>();
            requestEnvelopes.Add(requestEnvelope);
        }

        return requestEnvelopes;
    }

    private void CountWebRequests(List<RequestEnvelope> requestEnvelopes)
    {
        var genericGetRequestCount = 0;
        var genericDeleteRequestCount = 0;
        var genericHeadRequestCount = 0;
        var genericPatchRequestCount = 0;
        var genericPostRequestCount = 0;
        var genericPutRequestCount = 0;
        var getAssetBundleWebRequestCount = 0;
        var getAudioClipWebRequestCount = 0;
        var getTextureWebRequestCount = 0;

        foreach (var requestEnvelope in requestEnvelopes)
            switch (requestEnvelope.WebRequestType)
            {
                case "GenericGetRequest":
                    genericGetRequestCount++;
                    break;
                case "GenericDeleteRequest":
                    genericDeleteRequestCount++;
                    break;
                case "GenericHeadRequest":
                    genericHeadRequestCount++;
                    break;
                case "GenericPatchRequest":
                    genericPatchRequestCount++;
                    break;
                case "GenericPostRequest":
                    genericPostRequestCount++;
                    break;
                case "GenericPutRequest":
                    genericPutRequestCount++;
                    break;
                case "GetAssetBundleWebRequest":
                    getAssetBundleWebRequestCount++;
                    break;
                case "GetAudioClipWebRequest":
                    getAudioClipWebRequestCount++;
                    break;
                case "GetTextureWebRequest":
                    getTextureWebRequestCount++;
                    break;
                default:
                    Debug.Log($"Unknown WebRequestType: {requestEnvelope.WebRequestType}");
                    break;
            }

// Sum all the counts
        var totalCount = genericGetRequestCount + genericDeleteRequestCount +
                         genericHeadRequestCount + genericPatchRequestCount +
                         genericPostRequestCount + genericPutRequestCount +
                         getAssetBundleWebRequestCount + getAudioClipWebRequestCount +
                         getTextureWebRequestCount;

// Output the results
        Debug.Log($"GenericGetRequest count: {genericGetRequestCount}");
        Debug.Log($"GenericDeleteRequest count: {genericDeleteRequestCount}");
        Debug.Log($"GenericHeadRequest count: {genericHeadRequestCount}");
        Debug.Log($"GenericPatchRequest count: {genericPatchRequestCount}");
        Debug.Log($"GenericPostRequest count: {genericPostRequestCount}");
        Debug.Log($"GenericPutRequest count: {genericPutRequestCount}");
        Debug.Log($"GetAssetBundleWebRequest count: {getAssetBundleWebRequestCount}");
        Debug.Log($"GetAudioClipWebRequest count: {getAudioClipWebRequestCount}");
        Debug.Log($"GetTextureWebRequest count: {getTextureWebRequestCount}");
        Debug.Log($"Total WebRequest count: {totalCount}");
    }
}