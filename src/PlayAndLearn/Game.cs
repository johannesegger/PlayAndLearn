﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Logging.Serilog;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PlayAndLearn.Models;
using PlayAndLearn.Utils;

namespace PlayAndLearn
{
    public static class Game
    {
        internal static MainWindow MainWindow { get; private set; }

        private static IDictionary<Player, IControl> playerToControlMap = new Dictionary<Player, IControl>();

        internal static IControl TryFindPlayerControl(Player player)
        {
            if (playerToControlMap.TryGetValue(player, out var control))
            {
                return control;
            }
            return null;
        }

        private static TimeSpan movementDelay = TimeSpan.FromMilliseconds(40);

        private static ICollection<Control> drawnLines = new List<Control>();

        public static void SetSlowMotion() => movementDelay = TimeSpan.FromSeconds(1);

        public static Models.Rectangle Bounds => new Models.Rectangle(
            new Position(-MainWindow.Scene.Bounds.Width / 2, -MainWindow.Scene.Bounds.Height / 2),
            new Models.Size(MainWindow.Scene.Bounds.Width, MainWindow.Scene.Bounds.Height));

        public static void ShowScene()
        {
            using (var signal = new ManualResetEventSlim())
            {
                var uiThread = new Thread(() =>
                {
                    var builder = AppBuilder
                        .Configure<App>()
                        .UsePlatformDetect()
                        .LogToDebug()
                        .SetupWithoutStarting();
                    MainWindow = new MainWindow();
                    MainWindow.Show();
                    signal.Set();
                    builder.Instance.Run(MainWindow);
                });
                uiThread.IsBackground = false;
                uiThread.Start();
                signal.Wait();
            }
        }

        public static IDisposable AddSprite(Player sprite)
        {
            var addedSprite = new SingleAssignmentDisposable();
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var d = new CompositeDisposable();

                var spriteControl = new Image()
                    .Do(p => p.ZIndex = 10)
                    .Do(p => p.Width = sprite.Size.Width)
                    .Do(p => p.Height = sprite.Size.Height)
                    .Do(p =>
                    {
                        using (var costume = sprite.CostumeFactory())
                        {
                            p.Source = new Bitmap(costume);
                        }
                    });
                
                MainWindow.Scene.AddChild(spriteControl)
                    .DisposeWith(d);

                playerToControlMap.AddUndoable(sprite, spriteControl)
                    .DisposeWith(d);

                var speechBubbleControl = CreateSpeechBubble();
                speechBubbleControl.IsVisible = false;
                MainWindow.Scene.AddChild(speechBubbleControl)
                    .DisposeWith(d);

                var center = new Position(
                    MainWindow.Scene.Bounds.Width / 2 - sprite.Size.Width / 2,
                    MainWindow.Scene.Bounds.Height / 2 - sprite.Size.Height / 2
                );

                var positionChanged = sprite
                    .Changed(p => p.Position)
                    .Select(p => new Position(p.X + center.X, p.Y + center.Y));

                var positionOrDirectionChanged = Observable
                    .CombineLatest(
                        positionChanged,
                        sprite.Changed(p => p.Direction),
                        (position, direction) => new { position, direction });


                MainWindow.PlayerPanel
                    .AddChild(
                        new DockPanel
                        {
                            Children =
                            {
                                new Image()
                                    .Do(p => p.Margin = new Thickness(10))
                                    .Do(p => p.Width = 30)
                                    .Do(p =>
                                    {
                                        using (var costume = sprite.CostumeFactory())
                                        {
                                            p.Source = new Bitmap(costume);
                                        }
                                    }),
                                new TextBlock()
                                    .Do(p => p.VerticalAlignment = VerticalAlignment.Center)
                                    .Do(p => p.Margin = new Thickness(10))
                                    .Subscribe(
                                        positionOrDirectionChanged.ObserveOn(AvaloniaScheduler.Instance),
                                        (p, o) => p.Text = $"X: {o.position.X:F2} | Y: {o.position.Y:F2} | ∠ {o.direction.Value:F2}°",
                                        d)
                            }
                        }
                    )
                    .DisposeWith(d);

                Observable
                    .CombineLatest(
                        positionChanged,
                        sprite.Changed(p => p.Pen),
                        (position, pen) => (position, pen)
                    )
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(((Position position, Models.Pen pen) p) =>
                    {
                        if (p.pen.IsOn)
                        {
                            var currentPosition = new Position(
                                Canvas.GetLeft(spriteControl),
                                Canvas.GetBottom(spriteControl));
                            var line = new Line
                            {
                                StartPoint = new Point(
                                    currentPosition.X + sprite.Size.Width / 2,
                                    MainWindow.Scene.Bounds.Height - currentPosition.Y - sprite.Size.Height / 2),
                                EndPoint = new Point(
                                    p.position.X + sprite.Size.Width / 2,
                                    MainWindow.Scene.Bounds.Height - p.position.Y - sprite.Size.Height / 2),
                                Stroke = new SolidColorBrush(new Color(0xFF, p.pen.Color.Red, p.pen.Color.Green, p.pen.Color.Blue)),
                                StrokeThickness = p.pen.Weight,
                                ZIndex = 5
                            };
                            MainWindow.Scene.Children.Add(line);
                            drawnLines.Add(line);
                        }
                        Canvas.SetLeft(spriteControl, p.position.X);
                        Canvas.SetBottom(spriteControl, p.position.Y);
                    })
                    .DisposeWith(d);

                sprite
                    .Changed(p => p.Direction)
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(direction =>
                    {
                        spriteControl.RenderTransform = new RotateTransform(360 - direction.Value);
                    })
                    .DisposeWith(d);

                Observable
                    .CombineLatest(
                        sprite.Changed(p => p.SpeechBubble),
                        positionChanged,
                        (speechBubble, position) => new { speechBubble, position }
                    )
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(p =>
                    {
                        SetSpeechBubbleText(speechBubbleControl, p.speechBubble.Text);
                        Canvas.SetLeft(speechBubbleControl, p.position.X + 70);
                        Canvas.SetBottom(speechBubbleControl, p.position.Y + spriteControl.Bounds.Height);

                        speechBubbleControl.IsVisible = p.speechBubble.Text != string.Empty;
                    })
                    .DisposeWith(d);

                // TODO it's not guaranteed that the following subscriptions run
                // after updating the UI, but it currently works, because subscriptions
                // seem to run in the same order as they are being set up.
                sprite
                    .Changed(p => p.Position)
                    .Subscribe(_ => Sleep(movementDelay.TotalMilliseconds))
                    .DisposeWith(d);
                sprite
                    .Changed(p => p.Direction)
                    .Subscribe(_ => Sleep(movementDelay.TotalMilliseconds))
                    .DisposeWith(d);

                sprite
                    .Changed(p => p.SpeechBubble.Duration)
                    .Where(p => p > TimeSpan.Zero)
                    .Subscribe(p =>
                    {
                        Sleep(p.TotalMilliseconds);
                        sprite.SpeechBubble = SpeechBubble.Empty;
                    })
                    .DisposeWith(d);

                addedSprite.Disposable = d;
            }).Wait();

            return addedSprite;
        }

        private static Control CreateSpeechBubble()
        {
            var grid = new Grid { MaxWidth = 300 };
            grid.LayoutUpdated += (s, e) =>
            {
                grid.RenderTransform = new TranslateTransform(-grid.Bounds.Width / 2, 0);
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(new Border
                {
                    Background = Brushes.WhiteSmoke,
                    CornerRadius = 5,
                    BorderThickness = 5,
                    BorderBrush = Brushes.Black,
                    Child = new TextBlock
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 15,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(10, 5)
                    }
                });
            var triangle = new Path
            {
                Fill = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                Data = PathGeometry.Parse("M0,0 L15,0 0,15")
            };
            Grid.SetRow(triangle, 1);
            grid.Children.Add(triangle);
            return grid;
        }

        private static void SetSpeechBubbleText(IControl speechBubbleControl, string text)
        {
            speechBubbleControl
                .FindVisualChildren<TextBlock>()
                .Single()
                .Text = text;
        }
        
        public static void ClearScene()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                MainWindow.Scene.Children.RemoveAll(drawnLines);
                drawnLines.Clear();
            }).Wait();
        }

        public static void ShowSceneAndAddTurtle()
        {
            Game.ShowScene();
            Game.AddSprite(Turtle.Default);
        }

        public static void Sleep(double durationInMilliseconds)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(durationInMilliseconds));
        }
    }
}
