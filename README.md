# HtmlToImage.NET

A chromium driver, based on cdp.

Example:
```c#
using var chromium = new HtmlConverter("path to chrome executable", 0 /* auto */);
using var tab = chromium.NewTab();

await tab.NavigateTo("https://www.baidu.com");
// or await tab.HtmlAsPage("<html> <head></head> <body>Hello world!</body> </html>");

var imageData = tab.TakePhotoOfCurrentPage();
// ^ image
```