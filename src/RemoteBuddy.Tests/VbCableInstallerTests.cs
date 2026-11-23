// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using RemoteBuddy.Platform.Windows.Audio;
using Xunit;

namespace RemoteBuddy.Tests;

public class VbCableInstallerTests
{
    private static readonly Uri Mirror = new("https://remote.zfdang.com/downloads/drivers/VBCABLE_Driver_Pack46.zip");
    private static readonly Uri Official = new("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip");
    private static readonly byte[] Package = [1, 2, 3];
    private static readonly string ExpectedHash = Convert.ToHexString(SHA256.HashData(Package));

    [Fact]
    public async Task DownloadUsesMirrorWithoutContactingOfficialSourceWhenVerified()
    {
        using var handler = new PackageHandler(_ => CreateResponse(Package));
        using var client = new HttpClient(handler);

        Assert.Equal(Package, await VbCableInstaller.DownloadVerifiedAsync(client, [Mirror, Official], ExpectedHash));
        Assert.Equal([Mirror], handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadFallsBackAfterMissingOrInvalidMirror(bool missing)
    {
        using var handler = new PackageHandler(url => url == Mirror
            ? missing ? new HttpResponseMessage(HttpStatusCode.NotFound) : CreateResponse([4, 5, 6])
            : CreateResponse(Package));
        using var client = new HttpClient(handler);

        Assert.Equal(Package, await VbCableInstaller.DownloadVerifiedAsync(client, [Mirror, Official], ExpectedHash));
        Assert.Equal([Mirror, Official], handler.Requests);
    }

    [Fact]
    public async Task DownloadRejectsPackageWhenNeitherSourceMatches()
    {
        using var handler = new PackageHandler(_ => CreateResponse([7, 8, 9]));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<IOException>(() => VbCableInstaller.DownloadVerifiedAsync(client, [Mirror, Official], ExpectedHash));
        Assert.Equal([Mirror, Official], handler.Requests);
    }

    private static HttpResponseMessage CreateResponse(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class PackageHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(respond(uri));
        }
    }
}
