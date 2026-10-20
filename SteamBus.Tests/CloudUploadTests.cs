using System.Net;
using System.Security.Cryptography;
using Playtron.Plugin;
using Steam.Cloud;
using Steam.Config;
using SteamKit2;
using SteamKit2.Internal;
using Tmds.DBus;

namespace SteamBus.Tests;

public class CloudUploadTests
{
  const uint App = 2183900;

  class FakeUploadApi(EResult begin = EResult.OK) : ICloudFileApi
  {
    public List<(string File, bool Succeeded)> Commits = [];

    public Task<CCloud_ClientBeginFileUpload_Response> ClientBeginFileUpload(uint appid, string filename, byte[] sha, uint file_size, uint raw_file_size, ulong time_stamp, ERemoteStoragePlatform platformsToSync, ulong upload_batch_id)
    {
      if (begin != EResult.OK) throw new CloudRefusedException("ClientBeginFileUpload", begin);
      var response = new CCloud_ClientBeginFileUpload_Response();
      var block = new ClientCloudFileUploadBlockDetails
      {
        url_host = "cloud.invalid",
        url_path = "/save",
        use_https = true,
        http_method = (int)EHTTPMethod.PUT,
        block_offset = 0,
        block_length = file_size,
      };
      // The headers Steam sends for Google Cloud Storage, as seen on 2026-10-20.
      foreach (var (name, value) in new[]
      {
        ("Content-Length", file_size.ToString()),
        ("Content-Type", "application/octet-stream"),
        ("Content-Disposition", $"inline; filename*=UTF-8''{Path.GetFileName(filename)};"),
        ("x-goog-content-length-range", $"{file_size},{file_size}"),
      })
        block.request_headers.Add(new ClientCloudFileUploadBlockDetails.HTTPHeaders { name = name, value = value });
      response.block_requests.Add(block);
      return Task.FromResult(response);
    }

    public Task<CCloud_ClientFileDownload_Response> DownloadFileAsync(uint appid, string filename) =>
      begin == EResult.OK
        ? Task.FromResult(new CCloud_ClientFileDownload_Response())
        : throw new CloudRefusedException("ClientFileDownload", begin);

    public Task<CCloud_ClientCommitFileUpload_Response?> CientCommitFileUpload(uint appid, bool transfer_succeeded, string filename, byte[] file_sha)
    {
      Commits.Add((filename, transfer_succeeded));
      // Steam answers a failed transfer's commit with Fail, which reads as null.
      return Task.FromResult(transfer_succeeded ? new CCloud_ClientCommitFileUpload_Response { file_committed = true } : null);
    }
  }

  class Storage(HttpStatusCode status) : HttpMessageHandler
  {
    public int Puts;
    public HttpRequestMessage? Last;
    public byte[] Body = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      Puts++;
      Last = request;
      Body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
      return new HttpResponseMessage(status);
    }
  }

  string dir = "";

  [SetUp]
  public void MakeDir() => dir = Directory.CreateTempSubdirectory("steambus-upload-").FullName;

  [TearDown]
  public void RemoveDir() => Directory.Delete(dir, true);

  (RemoteCacheFile File, string Path) Save(string content)
  {
    var path = Path.Join(dir, "story_progression.cfg");
    File.WriteAllText(path, content);
    var bytes = File.ReadAllBytes(path);
    var info = new CCloud_AppFileInfo { sha_file = SHA1.HashData(bytes), raw_file_size = (uint)bytes.Length, time_stamp = 1792440057 };
    return (new RemoteCacheFile(info, "%WinAppDataLocal%Saber/story_progression.cfg"), path);
  }

  [Test]
  public async Task AFailedTransferIsStillCommittedSoTheFileIsNotLeftBusy()
  {
    var (file, path) = Save("progress");
    var api = new FakeUploadApi();
    var storage = new Storage(HttpStatusCode.ServiceUnavailable);
    var (_, error) = await CloudUtils.UploadFileAsync(App, file, path, 1, new SemaphoreSlim(1), new HttpClient(storage), api);
    Assert.That(storage.Puts, Is.EqualTo(1));
    Assert.That(error, Is.InstanceOf<HttpRequestException>());
    Assert.That(api.Commits, Is.EqualTo(new[] { (file.GetRemotePath(), false) }));
  }

  [Test]
  public async Task ATransferThatThrowsIsStillCommitted()
  {
    var (file, path) = Save("progress");
    var api = new FakeUploadApi();
    var client = new HttpClient(new ThrowingStorage());
    var (_, error) = await CloudUtils.UploadFileAsync(App, file, path, 1, new SemaphoreSlim(1), client, api);
    Assert.That(error, Is.InstanceOf<HttpRequestException>());
    Assert.That(api.Commits.Single().Succeeded, Is.False);
  }

  class ThrowingStorage : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      throw new HttpRequestException("connection reset");
  }

  [Test]
  public async Task ASuccessfulTransferIsCommittedAsSucceeded()
  {
    var (file, path) = Save("progress");
    var api = new FakeUploadApi();
    var storage = new Storage(HttpStatusCode.OK);
    var (_, error) = await CloudUtils.UploadFileAsync(App, file, path, 1, new SemaphoreSlim(1), new HttpClient(storage), api);
    Assert.That(error, Is.Null);
    Assert.That(api.Commits, Is.EqualTo(new[] { (file.GetRemotePath(), true) }));
    Assert.That(storage.Body, Is.EqualTo("progress"u8.ToArray()));
  }

  [Test]
  public async Task ASaveOverHalfAMegabyteIsSentWhole()
  {
    var path = Path.Join(dir, "gamedata.sav");
    var bytes = RandomNumberGenerator.GetBytes(689_104);
    File.WriteAllBytes(path, bytes);
    var info = new CCloud_AppFileInfo { sha_file = SHA1.HashData(bytes), raw_file_size = (uint)bytes.Length };
    var file = new RemoteCacheFile(info, "%WinAppDataRoaming%SEGA/gamedata.sav");
    var storage = new Storage(HttpStatusCode.OK);
    var api = new FakeUploadApi();
    var (_, error) = await CloudUtils.UploadFileAsync(App, file, path, 1, new SemaphoreSlim(1), new HttpClient(storage), api);
    Assert.That(error, Is.Null);
    Assert.That(storage.Body, Is.EqualTo(bytes));
    Assert.That(api.Commits.Single().Succeeded, Is.True);
  }

  [Test]
  public async Task SteamsUploadHeadersAreSentAsGiven()
  {
    var (file, path) = Save("progress");
    var storage = new Storage(HttpStatusCode.OK);
    var (_, error) = await CloudUtils.UploadFileAsync(App, file, path, 1, new SemaphoreSlim(1), new HttpClient(storage), new FakeUploadApi());
    Assert.That(error, Is.Null);
    var content = storage.Last!.Content!.Headers;
    Assert.That(content.GetValues("Content-Disposition").Single(), Is.EqualTo("inline; filename*=UTF-8''story_progression.cfg;"));
    Assert.That(content.GetValues("Content-Type").Single(), Is.EqualTo("application/octet-stream"));
    Assert.That(content.ContentLength, Is.EqualTo(8));
    Assert.That(storage.Last.Headers.GetValues("x-goog-content-length-range").Single(), Is.EqualTo("8,8"));
  }

  [Test]
  public async Task AFileSteamRefusesIsReportedAsBusyAndNothingIsSent()
  {
    var (file, path) = Save("progress");
    var api = new FakeUploadApi(EResult.Busy);
    var storage = new Storage(HttpStatusCode.OK);
    var (_, error) = await CloudUtils.UploadFileAsync(App, file, path, 1, new SemaphoreSlim(1), new HttpClient(storage), api);
    Assert.That(storage.Puts, Is.Zero);
    Assert.That(api.Commits, Is.Empty);
    Assert.That(CloudSyncCompletion.FileError(error!, null, SyncState.Upload), Is.EqualTo(DbusErrors.CloudBusy));
  }

  [Test]
  public async Task AFileSteamWillNotHandOutIsReportedAsBusy()
  {
    var (file, path) = Save("progress");
    var (_, error) = await CloudUtils.DownloadFileAsync(App, file, path, new SemaphoreSlim(1),
      new HttpClient(new Storage(HttpStatusCode.OK)), new FakeUploadApi(EResult.Busy));
    Assert.That(CloudSyncCompletion.FileError(error!, null, SyncState.Download), Is.EqualTo(DbusErrors.CloudBusy));
  }

  [Test]
  public void BusyOutranksAnEarlierFailureAndOtherRefusalsStayGeneric()
  {
    var busy = new CloudRefusedException("ClientBeginFileUpload", EResult.Busy);
    Assert.That(CloudSyncCompletion.FileError(busy, DbusErrors.CloudFileUpload, SyncState.Upload), Is.EqualTo(DbusErrors.CloudBusy));
    Assert.That(CloudSyncCompletion.FileError(new CloudRefusedException("ClientBeginFileUpload", EResult.Fail), null, SyncState.Upload),
      Is.EqualTo(DbusErrors.CloudFileUpload));
    Assert.That(CloudSyncCompletion.FileError(new HttpRequestException(), null, SyncState.Download), Is.EqualTo(DbusErrors.CloudFileDownload));
  }

  [Test]
  public void ABusyUploadFailsWithItsOwnError()
  {
    List<CloudSyncFailure> failures = [];
    var e = Assert.ThrowsAsync<DBusException>(() => CloudSyncCompletion.CompleteUploadAsync("2183900",
      DbusErrors.CloudBusy, () => Task.FromResult(true), () => { }, _ => { }, failures.Add));
    Assert.That(e!.ErrorName, Is.EqualTo(DbusErrors.CloudBusy));
    Assert.That(failures.Single().Error, Is.EqualTo(DbusErrors.CloudBusy));
    Assert.That(failures.Single().SyncState, Is.EqualTo((uint)SyncState.Upload));
  }

  [Test]
  public void ABusyDownloadFailsWithItsOwnError()
  {
    List<CloudSyncFailure> failures = [];
    var persisted = false;
    var e = Assert.ThrowsAsync<DBusException>(() => CloudSyncCompletion.CompleteDownloadAsync("2183900",
      DbusErrors.CloudBusy, () => persisted = true, _ => { }, failures.Add));
    Assert.That(e!.ErrorName, Is.EqualTo(DbusErrors.CloudBusy));
    Assert.That(persisted, Is.False);
    Assert.That(failures.Single().SyncState, Is.EqualTo((uint)SyncState.Download));
  }
}
