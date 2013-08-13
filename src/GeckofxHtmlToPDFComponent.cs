﻿using System;
using System.ComponentModel;
using System.IO;
using Gecko;

namespace GeckofxHtmlToPdf
{
	/* NB: I am currently facing a problem in having a component using a geckofx in a client which is
	 * also using it (which was my original use case)... setting "gfx.direct2d.disabled" to true, as we
	 * have to do, was giving me a COM crash in the client app, which, by the time it used this, had
	 * already been using the browser for other purposes. So for now, I'm making this class private again.
	 * If someone really needed it, they could make it public again, particularly if they aren't already
	 * using geckofx in their main project. But for me, it's not worth maintaining/documenting at this
	 * point, as my client app is going to have to use this as a command-line. Note, Hindle couldn't
	 * reproduce this in the geckofx sample app, so there's a mystery to be solved some day.
	 * 
	 * Why is this a component? Only becuase the geckobrowser that we use, even though it is invisible,
	* expects to be operating on the UI thread, getting events from the Application.DoEvents() loop, etc.
	* So we can't be making pdfs on a background thread. Having it as a component with a timer and
	* an event to signal when it is done makes it easy for programmer incorporating this see how to use it properly.
	 * 
	 * This component is used by the ConversionProgress form in this assembly for when the exe is used
	 * on the command line. But it can also be used by any other winforms app that references our assembly
	 * (even though it is an exe, not the usual dll). Just drag the component onto some other form, then
	 * call Start();
	*/

	public partial class GeckofxHtmlToPdfComponent : Component, nsIWebProgressListener
	{
		private ConversionOrder _conversionOrder;
		private GeckoWebBrowser _browser;
		private string _pathToTempPdf;
		private bool _finished;
		public event EventHandler Finished;
		public event EventHandler<PdfMakingStatus> StatusChanged;
		public string Status { get; private set; }

		public GeckofxHtmlToPdfComponent()
		{
			InitializeComponent();
		}

		public GeckofxHtmlToPdfComponent(IContainer container)
		{
			container.Add(this);

			InitializeComponent();
		}

		/// <summary>
		/// The path to the XulRunner directory. Must match the version that this exe was compiled for, specifically, the version that goes with the included Geckofx-Core and Geckofx-Winforms.
		/// </summary>
		public void Initialize(string pathToXulRunnerFolder)
		{
			Gecko.Xpcom.Initialize(pathToXulRunnerFolder);
		}

		/// <summary>
		/// On the application event thread, work on creating the pdf. Will raise the StatusChanged and Finished events
		/// </summary>
		/// <param name="conversionOrder"></param>
		public void Start(ConversionOrder conversionOrder)
		{
			if (!Gecko.Xpcom.IsInitialized)
			{
				throw new ApplicationException("Developer: you must call Initialize(pathToXulRunnerFolder), or do your own Gecko.Xpcom.Initialize(), before calling Start()");
			}

			//without this, we get invisible (white?) text on some machines
			Gecko.GeckoPreferences.User["gfx.direct2d.disabled"] = true;

			if (conversionOrder.EnableGraphite)
				GeckoPreferences.User["gfx.font_rendering.graphite.enabled"] = true;

			_conversionOrder = conversionOrder;
			_browser = new GeckoWebBrowser();
			this.components.Add(_browser);//so it gets disposed when we are

			_browser.JavascriptError += OnJavascriptError;

			if (conversionOrder.Debug)
			{
				_browser.ConsoleMessage += OnBrowserConsoleMessage;
			}
			
			var tempFileName = Path.GetTempFileName();
			File.Delete(tempFileName);
			_pathToTempPdf = tempFileName + ".pdf"; 
			File.Delete(_conversionOrder.OutputPath);
			_checkForBrowserNavigatedTimer.Enabled = true;
			Status = "Loading Html...";
			_browser.Navigate(_conversionOrder.InputPath);
		}

		void OnJavascriptError(object sender, JavascriptErrorEventArgs e)
		{
			if (_conversionOrder.Debug)
			{
				Console.WriteLine("GeckofxHtmlToPdf: {0} Line {1} Position {2}: {3}",e.Filename,e.Line,e.Pos,e.Message); 
			}
		}

		protected virtual void RaiseStatusChanged(PdfMakingStatus e)
		{
			var handler = StatusChanged;
			if (handler != null) handler(this, e);
		}

		protected virtual void RaiseFinished()
		{
			var handler = Finished;
			if (handler != null) handler(this, EventArgs.Empty);
		}

		private void OnBrowserConsoleMessage(object sender, ConsoleMessageEventArgs e)
		{
			//review: this won't do anything if we're not in command-line mode...
			//maybe a better design would be to rais an event that the consumer can
			//do something with, e.g. the command-line ConversionProgress form could
			//just turn around and write to the console
			Console.WriteLine(e.Message);
		}

		private void StartMakingPdf()
		{
			nsIWebBrowserPrint print = Xpcom.QueryInterface<nsIWebBrowserPrint>(_browser.Window.DomWindow);

			var service = Xpcom.GetService<nsIPrintSettingsService>("@mozilla.org/gfx/printsettings-service;1");
			var printSettings = service.GetNewPrintSettingsAttribute();

			printSettings.SetToFileNameAttribute(_pathToTempPdf);
			printSettings.SetPrintSilentAttribute(true); //don't show a printer settings dialog
			printSettings.SetShowPrintProgressAttribute(false);

			if (_conversionOrder.PageHeightInMillimeters > 0)
			{
				printSettings.SetPaperHeightAttribute(_conversionOrder.PageHeightInMillimeters);
				printSettings.SetPaperWidthAttribute(_conversionOrder.PageWidthInMillimeters);
				printSettings.SetPaperSizeUnitAttribute(1); //0=in, >0 = mm
			}
			else
			{
				printSettings.SetPaperSizeUnitAttribute(1);
				//doesn't actually work.  Probably a problem in the geckofx wrapper
				//printSettings.SetPaperNameAttribute(_conversionOrder.PageSizeName);
				if (_conversionOrder.PageSizeName.ToLower() == "a4")
				{
					printSettings.SetPaperWidthAttribute(210);
					printSettings.SetPaperHeightAttribute(297);
				}
				else if (_conversionOrder.PageSizeName.ToLower() == "a5")
				{
					printSettings.SetPaperWidthAttribute(148);
					printSettings.SetPaperHeightAttribute(210);
				}
				else if (_conversionOrder.PageSizeName.ToLower() == "a3")
				{
					printSettings.SetPaperWidthAttribute(297);
					printSettings.SetPaperHeightAttribute(420);
				}
				else if (_conversionOrder.PageSizeName.ToLower() == "b5")
				{
					printSettings.SetPaperWidthAttribute(176);
					printSettings.SetPaperHeightAttribute(250);
				}
				else if (_conversionOrder.PageSizeName.ToLower() == "letter")
				{
					printSettings.SetPaperWidthAttribute(215.9);
					printSettings.SetPaperHeightAttribute(279.4);
				}
				else if (_conversionOrder.PageSizeName.ToLower() == "legal")
				{
					printSettings.SetPaperWidthAttribute(215.9);
					printSettings.SetPaperHeightAttribute(355.6);
				}
				else
					throw new ApplicationException(
						"Sorry, currently GeckofxHtmlToPDF has a very limited set of paper sizes it knows about. Consider using the page-height and page-width arguments instead");
			}
			//this seems to be in inches, and doesn't have a unit-setter (unlike the paper size ones)
			const double kMillimetersPerInch = 25; //TODO what is it, exactly?
			printSettings.SetMarginTopAttribute(_conversionOrder.TopMarginInMillimeters/kMillimetersPerInch);
			printSettings.SetMarginBottomAttribute(_conversionOrder.BottomMarginInMillimeters/kMillimetersPerInch);
			printSettings.SetMarginLeftAttribute(_conversionOrder.LeftMarginInMillimeters/kMillimetersPerInch);
			printSettings.SetMarginRightAttribute(_conversionOrder.RightMarginInMillimeters/kMillimetersPerInch);


			printSettings.SetDownloadFontsAttribute(true); //review: what's this for?
			printSettings.SetOrientationAttribute(_conversionOrder.Landscape ? 1 : 0);
			printSettings.SetHeaderStrCenterAttribute("");
			printSettings.SetHeaderStrLeftAttribute("");
			printSettings.SetHeaderStrRightAttribute("");
			printSettings.SetFooterStrRightAttribute("");
			printSettings.SetFooterStrLeftAttribute("");
			printSettings.SetFooterStrCenterAttribute("");

			//TODO: doesn't seem to do anything. Probably a problem in the geckofx wrapper
			printSettings.SetScalingAttribute(_conversionOrder.Zoom);
			printSettings.SetOutputFormatAttribute(2); // 2 == kOutputFormatPDF

			Status = "Making PDF..";

			print.Print(printSettings, this);
			_checkForPdfFinishedTimer.Enabled = true;
		}

		private void FinishMakingPdf()
		{
			if (!File.Exists(_pathToTempPdf))
				throw new ApplicationException(
					"GeckoFxHtmlToPdf was not able to create the PDF.\r\n\r\nDetails: Gecko did not produce the expected document.");

			try
			{
				File.Move(_pathToTempPdf, _conversionOrder.OutputPath);
				RaiseFinished();
			}
			catch (IOException e)
			{
				//TODO: we can get here for a different reason: the source file is still in use
				throw new ApplicationException(
					string.Format(
						"Tried to move the file {0} to {1}, but the Operating System said that one of these files was locked. Please try again.\r\n\r\nDetails: {1}",
						_pathToTempPdf, _conversionOrder.OutputPath, e.Message));
			}
		}

		private void OnCheckForBrowserNavigatedTimerTick(object sender, EventArgs e)
		{
			if (_browser.Document != null && _browser.Document.ActiveElement != null)
			{
				_checkForBrowserNavigatedTimer.Enabled = false;
				StartMakingPdf();
			}
		}

		private void OnCheckForPdfFinishedTimer_Tick(object sender, EventArgs e)
		{
			if (_finished)
			{
				_checkForPdfFinishedTimer.Enabled = false;
				FinishMakingPdf();
			}
		}


		public void OnStateChange(nsIWebProgress aWebProgress, nsIRequest aRequest, uint aStateFlags, int aStatus)
		{
			_finished = (aStateFlags & nsIWebProgressListenerConstants.STATE_STOP) != 0;
		}

		#region nsIWebProgressListener

		public void OnProgressChange(nsIWebProgress webProgress, nsIRequest request, int currentSelfProgress,
		                             int maxSelfProgress,
		                             int currentTotalProgress, int maxTotalProgress)
		{
			if (maxTotalProgress == 0)
				return;

			// if we use the maxTotalProgress, the problem is that it starts off below 100, the jumps to 100 at the end
			// so it looks a lot better to just always scale, to 100, the current progress by the max at that point
			RaiseStatusChanged(new PdfMakingStatus()
				{
					percentage = (int) (100.0*(currentTotalProgress)/maxTotalProgress),
					statusLabel = Status
				});
		}

		public void OnLocationChange(nsIWebProgress aWebProgress, nsIRequest aRequest, nsIURI aLocation, uint aFlags)
		{
		}

		public void OnStatusChange(nsIWebProgress aWebProgress, nsIRequest aRequest, int aStatus, string aMessage)
		{
		}

		public void OnSecurityChange(nsIWebProgress aWebProgress, nsIRequest aRequest, uint aState)
		{
		}

		#endregion
	}

	public class PdfMakingStatus : EventArgs
	{
		public int percentage;
		public string statusLabel;
	}
}

