﻿using Microsoft.Azure.CognitiveServices.Vision.Face;
using Microsoft.Azure.CognitiveServices.Vision.Face.Models;
using OpenCvSharp;
using RealTimeFaceApi.Core.Data;
using RealTimeFaceApi.Core.Filters;
using RealTimeFaceApi.Core.Trackers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RealTimeFaceApi.Cmd
{
    public static class Program
    {
        // TODO: Add Face API subscription key.
        private static string FaceSubscriptionKey = "";

        // TODO: Add face group ID.
        private static string FaceGroupId = "";

        private static readonly Scalar _faceColorBrush = new Scalar(0, 0, 255);
        private static FaceClient _faceClient;
        private static Task _faceRecognitionTask = null;
        private static IList<Person> _cachedIdentities = null;

        public static void Main(string[] args)
        {
            _faceClient = new FaceClient(new ApiKeyServiceClientCredentials(FaceSubscriptionKey))
            {
                Endpoint = "https://westus.api.cognitive.microsoft.com"
            };

            string filename = args.FirstOrDefault();
            Run(filename);
        }

        private static void Run(string filename)
        {
            int timePerFrame;
            VideoCapture capture;
            if (!string.IsNullOrWhiteSpace(filename) && File.Exists(filename))
            {
                // If filename exists, use that as a source of video.
                capture = InitializeVideoCapture(filename);

                // Allow just enough time to paint the frame on the window.
                timePerFrame = 1;
            }
            else
            {
                // Otherwise use the webcam.
                capture = InitializeCapture(0);
                
                // Time required to wait until next frame.
                timePerFrame = (int)Math.Round(1000 / capture.Fps);
            }

            // Input was not initialized.
            if (capture == null)
            {
                Console.ReadKey();
                return;
            }

            // Initialize face detection algorithm.
            CascadeClassifier haarCascade = InitializeFaceClassifier();
            
            // List of simple face filtering algorithms.
            var filtering = new SimpleFaceFiltering(new IFaceFilter[]
            {
                new TooSmallFacesFilter(20, 20)
            });

            // List of simple face tracking algorithms.
            var trackingChanges = new SimpleFaceTracking(new IFaceTrackingChanged[]
            {
                new TrackNumberOfFaces(),
                new TrackDistanceOfFaces { Threshold = 2000 }
            });

            // Open a new window via OpenCV.
            using (Window window = new Window("capture"))
            {
                using (Mat image = new Mat())
                {
                    while (true)
                    {
                        // Get current frame.
                        capture.Read(image);
                        if (image.Empty())
                            continue;

                        // Detect faces
                        var faces = DetectFaces(haarCascade, image);

                        // Filter faces
                        var state = faces.ToImageState();
                        state = filtering.FilterFaces(state);

                        // Determine change
                        var hasChange = trackingChanges.ShouldUpdateRecognition(state);

                        if (hasChange)
                        {
                            Console.WriteLine("Changes detected...");

                            // Identify faces if changed and previous identification finished.
                            if (_faceRecognitionTask == null && !string.IsNullOrWhiteSpace(FaceSubscriptionKey))
                            {
                                _faceRecognitionTask = StartRecognizing(image);
                            }
                        }

                        using (var renderedFaces = RenderFaces(state, image))
                        {
                            // Update popup window.
                            window.ShowImage(renderedFaces);
                        }

                        // Wait for next frame and allow Window to be repainted.
                        Cv2.WaitKey(timePerFrame);
                    }
                }
            }
        }

        /// <summary>
        /// Use Microsoft Cognitive Services to recognize their faces.
        /// </summary>
        /// <param name="image">Video or web cam frame.</param>
        private static async Task StartRecognizing(Mat image)
        {
            try
            {
                Console.WriteLine(DateTime.Now + ": Attempting to recognize faces...");
                
                var stream = image.ToMemoryStream();
                var detectedFaces = await _faceClient.Face.DetectWithStreamAsync(stream, true, true);
                var faceIds = detectedFaces.Where(f => f.FaceId.HasValue).Select(f => f.FaceId.Value).ToList();

                if (faceIds.Any())
                {
                    var potentialUsers = await _faceClient.Face.IdentifyAsync(faceIds, FaceGroupId);
                    foreach (var candidate in potentialUsers.Select(u => u.Candidates.FirstOrDefault()))
                    {
                        var candidateName = await GetCandidateName(candidate?.PersonId);
                        Console.WriteLine($"{DateTime.Now}: {candidateName} ({candidate?.PersonId})");
                    }
                }
            }
            catch (APIErrorException apiError)
            {
                Console.WriteLine("Cognitive service error: " + apiError?.Body?.Error?.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Getting identity failed: " + e.ToString());
            }

            _faceRecognitionTask = null;
        }

        private async static Task<string> GetCandidateName(Guid? personId)
        {
            if (!personId.HasValue)
            {
                return "No Person ID";
            }

            if (_cachedIdentities == null)
            {
                _cachedIdentities = await _faceClient.PersonGroupPerson.ListAsync(FaceGroupId);
            }

            return _cachedIdentities
                ?.Where(i => i.PersonId == personId)
                .Select(i => i.Name)
                .FirstOrDefault()
                ?? "Candidate not found";
        }

        /// <summary>
        /// Initialize classifier used for offline face detection.
        /// </summary>
        private static CascadeClassifier InitializeFaceClassifier()
        {
            return new CascadeClassifier("Data/haarcascade_frontalface_alt.xml");
        }

        /// <summary>
        /// Initialize web cam capture.
        /// </summary>
        /// <returns>Returns web cam capture.</returns>
        private static VideoCapture InitializeCapture(int cameraIndex = 0)
        {
            VideoCapture capture = new VideoCapture();
            capture.Open(CaptureDevice.MSMF, cameraIndex);

            if (!capture.IsOpened())
            {
                Console.WriteLine("Unable to open capture.");
                return null;
            }

            return capture;
        }

        /// <summary>
        /// Initializes video capture for video files.
        /// </summary>
        /// <param name="file">Path to a video.</param>
        /// <returns>Return video file capture.</returns>
        private static VideoCapture InitializeVideoCapture(string file)
        {
            var capture = new VideoCapture(file);
            if (!capture.IsOpened())
            {
                Console.WriteLine("Unable to open video file {0}.", file);
                return null;
            }

            return capture;
        }

        /// <summary>
        /// Use OpenCV Cascade classifier to do offline face detection.
        /// </summary>
        /// <param name="cascadeClassifier">OpenCV cascade classifier.</param>
        /// <param name="image">Web cam or video frame.</param>
        /// <returns>Return list of faces as rectangles.</returns>
        private static Rect[] DetectFaces(CascadeClassifier cascadeClassifier, Mat image)
        {
            return cascadeClassifier
                .DetectMultiScale(
                    image,
                    1.08,
                    2,
                    HaarDetectionType.ScaleImage,
                    new Size(60, 60));
        }

        /// <summary>
        /// Render detected faces via OpenCV.
        /// </summary>
        /// <param name="state">Current frame state.</param>
        /// <param name="image">Web cam or video frame.</param>
        /// <returns>Returns new image frame.</returns>
        private static Mat RenderFaces(FrameState state, Mat image)
        {
            Mat result = image.Clone();
            Cv2.CvtColor(image, image, ColorConversionCodes.BGR2GRAY);

            // Render all detected faces
            foreach (var face in state.Faces)
            {
                var face1_1 = new Point
                {
                    X = face.Center.X - (int)(face.Size.Height * 0.7),
                    Y = face.Center.Y - (int)(face.Size.Height * 0.7)
                };
                var face1_2 = new Point
                {
                    X = face.Center.X + (int)(face.Size.Width * 0.7),
                    Y = face.Center.Y + (int)(face.Size.Height * 0.8)
                };
                var face2_1 = new Point
                {
                    X = face.Center.X - (int)(face.Size.Width * 0.7),
                    Y = face.Center.Y - (int)(face.Size.Height * 0.7)
                };
                var face2_2 = new Point
                {
                    X = face.Center.X + (int)(face.Size.Width * 0.7),
                    Y = face.Center.Y - (int)(face.Size.Height * 0.7) + 20
                };
                var face3_1 = new Point
                {
                    X = face.Center.X - (int)(face.Size.Width * 0.7),
                    Y = face.Center.Y - (int)(face.Size.Height * 0.7) + 15
                };
                var axes = new Size
                {
                    Width = (int)(face.Size.Width * 0.5) + 10,
                    Height = (int)(face.Size.Height * 0.5) + 10
                };

                Cv2.Rectangle(result, face1_1, face1_2, new Scalar(80, 18, 236), 2);
                Cv2.Rectangle(result, face2_1, face2_2, new Scalar(80, 18, 236), Cv2.FILLED);
                Cv2.PutText(result, "Recognized...", face3_1, 0, 0.4, new Scalar(255, 255, 255));
            }

            return result;
        }
    }
}
