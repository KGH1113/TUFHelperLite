using System;
using System.Net;
using System.Text;

namespace TUFHelperLite.Infrastructure.Downloads;

public static class DownloadUrlResolver
{
  public static string Resolve(string url, WebClient client)
  {
    try
    {
      DownloadUrlPolicy.Validate(url);
      string resolvedUrl;

      if (url.Contains("cdn.discordapp.com"))
      {
        resolvedUrl = url.Replace("cdn.discordapp.com", "fixcdn.hyonsu.com");
      }
      else if (url.StartsWith("https://drive.google.com/file/d/") ||
          url.StartsWith("https://drive.google.com/open?id=") ||
          url.StartsWith("https://drive.google.com/u/0/uc"))
      {
        resolvedUrl = ResolveGoogleDrive(url, client);
      }
      else if (url.StartsWith("https://www.mediafire.com"))
      {
        string html = client.DownloadString(url);
        int start = html.IndexOf("https://download", StringComparison.Ordinal);
        int end = StringUtil.GetNextIndexOf('"', html, start);

        if (start < 0 || end <= start)
        {
          throw new InvalidOperationException("MediaFire direct URL was not found.");
        }

        resolvedUrl = html.Substring(start, end - start);
      }
      else if (url.StartsWith("https://www.dropbox.com"))
      {
        string id = StringUtil.GetValue(url, "https://www.dropbox.com/s/", "?");
        resolvedUrl = $"https://www.dropbox.com/s/{id}?dl=1";
      }
      else if (url.StartsWith("https://drive.google.com/drive/folders/"))
      {
        throw new NotSupportedException("Google Drive folders are not supported directly.");
      }
      else if (url.StartsWith("https://steamcommunity.com/"))
      {
        throw new NotSupportedException("Steam Workshop links are not supported directly.");
      }
      else
      {
        resolvedUrl = url;
      }

      DownloadUrlPolicy.Validate(resolvedUrl);
      return resolvedUrl;
    }
    catch (Exception e)
    {
      throw new InvalidOperationException($"The download link is not accessible. {e.Message}", e);
    }
  }

  private static string ResolveGoogleDrive(string url, WebClient client)
  {
    if (url.StartsWith("https://drive.google.com/u/0/uc"))
    {
      return ResolveLargeGoogleDriveFile(url, client);
    }

    string id = "";

    if (url.Contains("/d/"))
    {
      if (url.Contains("/view")) id = StringUtil.GetValue(url, "/d/", "/view");
      else if (url.Contains("/edit")) id = StringUtil.GetValue(url, "/d/", "/edit");
    }

    if (url.Contains("id="))
    {
      id = url.Split(new[] { "id=" }, StringSplitOptions.None)[1];
      if (id.Contains("&")) id = id.Split('&')[0];
    }

    if (string.IsNullOrWhiteSpace(id))
    {
      throw new InvalidOperationException("Google Drive file id was not resolved.");
    }

    string downloadUrl = $"https://drive.google.com/u/0/uc?export=download&id={id}";

    using (System.IO.Stream stream = client.OpenRead(downloadUrl))
    {
      byte[] buffer = new byte[15];
      stream.Read(buffer, 0, buffer.Length);

      if (Encoding.UTF8.GetString(buffer) == "<!DOCTYPE html>")
      {
        return ResolveLargeGoogleDriveFile(downloadUrl, client);
      }
    }

    return downloadUrl;
  }

  private static string ResolveLargeGoogleDriveFile(string url, WebClient client)
  {
    string html = client.DownloadString(url);
    string id = StringUtil.GetValue(html, "name=\"id\" value=\"", "\">");
    string uuid = StringUtil.GetValue(html, "name=\"uuid\" value=\"", "\">");

    if (!html.Contains("name=\"at\" value=\""))
    {
      return $"https://drive.usercontent.google.com/download?id={id}&export=download&authuser=0&confirm=t&uuid={uuid}";
    }

    string at = StringUtil.GetValue(html, "name=\"at\" value=\"", "\">");
    return $"https://drive.usercontent.google.com/download?id={id}&export=download&authuser=0&confirm=t&uuid={uuid}&at={at}";
  }
}
