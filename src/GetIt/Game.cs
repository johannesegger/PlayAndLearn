﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Logging.Serilog;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Elmish.Net;
using Elmish.Net.VDom;
using GetIt.Models;
using GetIt.Utils;
using static Elmish.Net.ElmishApp<GetIt.Models.Message>;

namespace GetIt
{
    public static class Game
    {
        private static readonly ISubject<Message> dispatchSubject = new Subject<Message>();
        public static State State { get; private set; }

        public static void ShowScene()
        {
            using (var signal = new ManualResetEventSlim())
            {
                var uiThread = new Thread(() =>
                {
                    var appBuilder = AppBuilder
                        .Configure<App>()
                        .UsePlatformDetect()
                        .LogToDebug()
                        .SetupWithoutStarting();
                        
                    var proxy = new Proxy();
                    var renderLoop = AvaloniaLocator.Current.GetService<IRenderLoop>();
                    var requestAnimationFrame = Observable
                        .FromEventPattern<EventArgs>(
                            h => renderLoop.Tick += h,
                            h => renderLoop.Tick -= h)
                        .Select(_ => Unit.Default);
                    ElmishApp.Run(
                        requestAnimationFrame,
                        Init(),
                        Update,
                        View,
                        Subscribe,
                        AvaloniaScheduler.Instance,
                        () => proxy.Window);

                    var cts = new CancellationTokenSource();
                    proxy.WindowChanged.Subscribe(window =>
                    {
                        window.Show();
                        window.Closed += (s, e) => cts.Cancel();
                        signal.Set();
                    });
                    appBuilder.Instance.Run(cts.Token);
                    Environment.Exit(0); // shut everything down when the UI thread exits
                });
                uiThread.IsBackground = false;
                uiThread.Start();
                signal.Wait();
            }
        }

        private static (State, Cmd<Message>) Init()
        {
            State = new State(
                new Models.Rectangle(new Position(-300, -200), new Models.Size(600, 400)),
                ImmutableList<Player>.Empty,
                ImmutableList<PenLine>.Empty,
                new Position(0, 0),
                ImmutableList<KeyDownHandler>.Empty,
                ImmutableList<ClickPlayerHandler>.Empty,
                ImmutableList<MouseEnterPlayerHandler>.Empty);
            var cmd = Cmd.None<Message>();
            return (State, cmd);
        }

        private static (State, Cmd<Message>) Update(Message message, State state)
        {
            var (newState, cmd) = UpdateCore(message, state);
            State = newState;
            return (State, cmd);
        }

        private static (State, Cmd<Message>) UpdateCore(Message message, State state)
        {
            State updatePlayer(State s, Guid playerId, Func<Player, Player> fn)
            {
                return s.With(
                    p => p.Players,
                    s.Players
                        .Select(player => player.Id == playerId ? fn(player) : player)
                        .ToImmutableList());
            }
            return message.Match(
                (Message.SetSceneSize m) =>
                {
                    var bounds = new Models.Rectangle(new Position(-m.Size.Width / 2, -m.Size.Height / 2), m.Size);
                    var newState = state.With(p => p.SceneBounds, bounds);
                    return (newState, Cmd.None<Message>());
                },
                (Message.SetMousePosition m) =>
                {
                    var newState = state.With(p => p.MousePosition, m.Position);
                    return (newState, Cmd.None<Message>());
                },
                (Message.SetPosition m) =>
                {
                    var currentPlayer = state.Players.Single(p => p.Id == m.PlayerId);
                    var newState = updatePlayer(state, m.PlayerId, player => player.With(p => p.Position, m.Position));
                    if (currentPlayer.Pen.IsOn)
                    {
                        var line = new PenLine(currentPlayer.Position, m.Position, currentPlayer.Pen.Weight, currentPlayer.Pen.Color);
                        newState = newState.With(p => p.PenLines, state.PenLines.Add(line));
                    }
                    return (newState, Cmd.None<Message>());
                },
                (Message.SetDirection m) =>
                {
                    var newState = updatePlayer(state, m.PlayerId, player => player.With(p => p.Direction, m.Angle));
                    return (newState, Cmd.None<Message>());
                },
                (Message.Say m) =>
                {
                    var newState = updatePlayer(state, m.PlayerId, player => player.With(p => p.SpeechBubble, m.SpeechBubble));
                    return (newState, Cmd.None<Message>());
                },
                (Message.SetPen m) =>
                {
                    var newState = updatePlayer(state, m.PlayerId, player => player.With(p => p.Pen, m.Pen));
                    return (newState, Cmd.None<Message>());
                },
                (Message.SetSizeFactor m) =>
                {
                    var newState = updatePlayer(state, m.PlayerId, player => player.With(p => p.SizeFactor, m.SizeFactor));
                    return (newState, Cmd.None<Message>());
                },
                (Message.AddPlayer m) =>
                {
                    var newState = state.With(p => p.Players, state.Players.Add(m.Player));
                    return (newState, Cmd.None<Message>());
                },
                (Message.RemovePlayer m) =>
                {
                    var newState = state.With(p => p.Players, state.Players.Where(p => p.Id != m.PlayerId));
                    return (newState, Cmd.None<Message>());
                },
                (Message.ClearScene m) =>
                {
                    var newState = state.With(p => p.PenLines, state.PenLines.Clear());
                    return (newState, Cmd.None<Message>());
                },
                (Message.AddKeyDownHandler m) =>
                {
                    var newState = state.With(p => p.KeyDownHandlers, state.KeyDownHandlers.Add(m.Handler));
                    return (newState, Cmd.None<Message>());
                },
                (Message.RemoveKeyDownHandler m) =>
                {
                    var newState = state.With(p => p.KeyDownHandlers, state.KeyDownHandlers.Remove(m.Handler));
                    return (newState, Cmd.None<Message>());
                },
                (Message.TriggerKeyDownEvent m) =>
                {
                    TaskPoolScheduler.Default.Schedule(() =>
                        state.KeyDownHandlers
                            .Where(p => p.Key == m.Key)
                            .ForEach(p => p.Handler()));
                    return (state, Cmd.None<Message>());
                },
                (Message.AddClickPlayerHandler m) =>
                {
                    var newState = state.With(p => p.ClickPlayerHandlers, state.ClickPlayerHandlers.Add(m.Handler));
                    return (newState, Cmd.None<Message>());
                },
                (Message.RemoveClickPlayerHandler m) =>
                {
                    var newState = state.With(p => p.ClickPlayerHandlers, state.ClickPlayerHandlers.Remove(m.Handler));
                    return (newState, Cmd.None<Message>());
                },
                (Message.TriggerClickPlayerEvent m) =>
                {
                    TaskPoolScheduler.Default.Schedule(() =>
                        state.ClickPlayerHandlers
                            .Where(p => p.PlayerId == m.PlayerId)
                            .ForEach(p => p.Handler()));
                    return (state, Cmd.None<Message>());
                },
                (Message.AddMouseEnterPlayerHandler m) =>
                {
                    var newState = state.With(p => p.MouseEnterPlayerHandlers, state.MouseEnterPlayerHandlers.Add(m.Handler));
                    return (newState, Cmd.None<Message>());
                },
                (Message.RemoveMouseEnterPlayerHandler m) =>
                {
                    var newState = state.With(p => p.MouseEnterPlayerHandlers, state.MouseEnterPlayerHandlers.Remove(m.Handler));
                    return (newState, Cmd.None<Message>());
                },
                (Message.TriggerMouseEnterPlayerEvent m) =>
                {
                    TaskPoolScheduler.Default.Schedule(() =>
                        state.MouseEnterPlayerHandlers
                            .Where(p => p.PlayerId == m.PlayerId)
                            .ForEach(p => p.Handler()));
                    return (state, Cmd.None<Message>());
                });
        }

        private static Lazy<WindowIcon> Icon = new Lazy<WindowIcon>(() =>
        {
            using (var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GetIt.icon.png"))
            {
                return new WindowIcon(iconStream);
            }
        });

        private static Position GetPosition(State state, Point screenCoordinate)
        {
            return new Position(
                state.SceneBounds.Left + screenCoordinate.X,
                state.SceneBounds.Top - screenCoordinate.Y);
        }

        private static double GetScreenCoordinateX(State state, Position position)
        {
            return position.X - state.SceneBounds.Left;
        }

        private static double GetScreenCoordinateY(State state, Position position)
        {
            return state.SceneBounds.Top - position.Y;
        }

        private static Point GetScreenCoordinate(State state, Position position)
        {
            return new Point(
                GetScreenCoordinateX(state, position),
                GetScreenCoordinateY(state, position));
        }

        private static IVDomNode<Window, Message> View(State state, Dispatch<Message> dispatch)
        {
            return VDomNode<Window>()
                .Set(p => p.FontFamily, "Segoe UI Symbol")
                .Set(p => p.Title, "GetIt")
                .Set(p => p.Icon, Icon.Value, EqualityComparer.Create((WindowIcon icon) => 0))
                .Set(p => p.Content, VDomNode<Canvas>()
                    .SetChildNodes(p => p.Children, GetSceneChildren(state, dispatch))
                    .Subscribe(p => Observable
                        .FromEventPattern(
                            h => p.LayoutUpdated += h,
                            h => p.LayoutUpdated -= h
                        )
                        .Select(_ => new Message.SetSceneSize(new Models.Size(p.Bounds.Width, p.Bounds.Height)))))
                .Subscribe(window => Observable
                    .Merge(
                        Observable
                            .FromEventPattern<PointerEventArgs>(
                                h => window.PointerMoved += h,
                                h => window.PointerMoved -= h)
                            .Select(p => p.EventArgs.Device.GetPosition(window)),
                        Observable
                            .FromEventPattern<VisualTreeAttachmentEventArgs>(
                                h => ((IVisual)window.Content).AttachedToVisualTree += h,
                                h => ((IVisual)window.Content).AttachedToVisualTree -= h)
                            .Select(p => ((IInputRoot)window).MouseDevice.GetPosition((IVisual)window.Content)))
                    .Select(p => new Message.SetMousePosition(GetPosition(state, p))))
                .Subscribe(window => Observable
                    .Create<KeyEventArgs>(observer => window
                        .AddHandler(
                            InputElement.KeyDownEvent,
                            new EventHandler<KeyEventArgs>((s, e) => observer.OnNext(e)),
                            handledEventsToo: true)
                    )
                    .Choose(e => e.Key
                        .TryGetKeyboardKey()
                        .Select(key => new Message.TriggerKeyDownEvent(key))));
        }

        private static IEnumerable<IVDomNode<Message>> GetSceneChildren(State state, Dispatch<Message> dispatch)
        {
            foreach (var player in state.Players)
            {
                yield return VDomNode<ContentControl>()
                    .Set(p => p.ZIndex, 10)
                    .Set(p => p.Width, player.Size.Width)
                    .Set(p => p.Height, player.Size.Height)
                    .Set(p => p.RenderTransform, VDomNode<RotateTransform>()
                        .Set(p => p.Angle, 360 - player.Direction.Value))
                    .Set(p => p.Content, GetPlayerView(player)
                        .Set(p => p.RenderTransform, VDomNode<ScaleTransform>()
                            .Set(p => p.ScaleX, player.Size.Width / player.Costume.Size.Width)
                            .Set(p => p.ScaleY, player.Size.Height / player.Costume.Size.Height)))
                    .Attach(Canvas.LeftProperty, GetScreenCoordinateX(state, player.Position) - player.Size.Width / 2)
                    .Attach(Canvas.BottomProperty, state.SceneBounds.Size.Height - GetScreenCoordinateY(state, player.Position) - player.Size.Height / 2);

                yield return VDomNode<Grid>()
                    .Set(p => p.MaxWidth, 300)
                    .Set(p => p.IsVisible, player.SpeechBubble.Text != string.Empty)
                    .Attach(Canvas.LeftProperty, GetScreenCoordinateX(state, player.Position) + 45)
                    .Attach(Canvas.BottomProperty, state.SceneBounds.Size.Height - GetScreenCoordinateY(state, player.Position) + player.Size.Height / 2)
                    .SetChildNodes(
                        p => p.RowDefinitions,
                        VDomNode<RowDefinition>().Set(p => p.Height, new GridLength(1, GridUnitType.Star)),
                        VDomNode<RowDefinition>().Set(p => p.Height, GridLength.Auto))
                    // TODO that's a bit ugly
                    .Subscribe(p => Observable
                        .FromEventPattern(
                            h => p.LayoutUpdated += h,
                            h => p.LayoutUpdated -= h
                        )
                        .Do(_ => p.RenderTransform = new TranslateTransform(-p.Bounds.Width / 2, 0))
                        .Select(_ => (Message)null)
                        .IgnoreElements()
                    )
                    .SetChildNodes(
                        p => p.Children,
                        VDomNode<Border>()
                            .Set(p => p.Background, VDomNode<SolidColorBrush>().Set(p => p.Color, Colors.WhiteSmoke))
                            .Set(p => p.CornerRadius, 5)
                            .Set(p => p.BorderThickness, 5)
                            .Set(p => p.BorderBrush, VDomNode<SolidColorBrush>().Set(p => p.Color, Colors.Black))
                            .Set(p => p.Child, VDomNode<TextBlock>()
                                .Set(p => p.HorizontalAlignment, HorizontalAlignment.Center)
                                .Set(p => p.VerticalAlignment, VerticalAlignment.Center)
                                .Set(p => p.FontSize, 15)
                                .Set(p => p.TextWrapping, TextWrapping.Wrap)
                                .Set(p => p.Margin, new Thickness(10, 5))
                                .Set(p => p.Text, player.SpeechBubble.Text)),
                        VDomNode<Path>()
                            .Set(p => p.Fill, VDomNode<SolidColorBrush>().Set(p => p.Color, Colors.Black))
                            .Set(p => p.HorizontalAlignment, HorizontalAlignment.Center)
                            .Set(p => p.Data,
                                // TODO simplify?
                                new VDomNode<StreamGeometry, Message>(
                                    () => PathGeometry.Parse("M0,0 L15,0 0,15"),
                                    ImmutableList<IVDomNodeProperty<StreamGeometry, Message>>.Empty,
                                    _ => Sub.None<Message>()))
                            .Attach(Grid.RowProperty, 1));
            }

            foreach (var line in state.PenLines)
            {
                yield return VDomNode<Line>()
                    .Set(p => p.StartPoint, GetScreenCoordinate(state, line.Start))
                    .Set(p => p.EndPoint, GetScreenCoordinate(state, line.End))
                    .Set(p => p.Stroke, VDomNode<SolidColorBrush>().Set(p => p.Color, line.Color.ToAvaloniaColor()))
                    .Set(p => p.StrokeThickness, line.Weight)
                    .Set(p => p.ZIndex, 5);
            }

            yield return VDomNode<WrapPanel>()
                .Attach(Canvas.LeftProperty, 0)
                .Attach(Canvas.BottomProperty, 0)
                .SetChildNodes(p => p.Children, VDomNode<DockPanel>()
                    .SetChildNodes(p => p.Children, GetPlayerInfo(state)));
        }

        private static IVDomNode<Panel, Message> GetPlayerView(Player player)
        {
            return VDomNode<Canvas>()
                .Set(p => p.Width, player.Costume.Size.Width)
                .Set(p => p.Height, player.Costume.Size.Height)
                .Subscribe(p => Observable
                    .Create<RoutedEventArgs>(observer =>
                        p.AddHandler(
                            InputElement.TappedEvent,
                            new EventHandler<RoutedEventArgs>((s, e) => observer.OnNext(e)),
                            handledEventsToo: true)
                    )
                    .Select(_ => new Message.TriggerClickPlayerEvent(player.Id)))
                .Subscribe(p => Observable
                    .Create<PointerEventArgs>(observer =>
                        p.AddHandler(
                            InputElement.PointerEnterEvent,
                            new EventHandler<PointerEventArgs>((s, e) => observer.OnNext(e)),
                            handledEventsToo: true)
                    )
                    .Select(_ => new Message.TriggerMouseEnterPlayerEvent(player.Id)))
                .SetChildNodes(p => p.Children, player.Costume.Paths
                    .Select(path => VDomNode<Path>()
                        .Set(p => p.Fill, VDomNode<SolidColorBrush>()
                            .Set(p => p.Color, path.Fill.ToAvaloniaColor()))
                        .Set(p => p.Data,
                            // TODO simplify?
                            new VDomNode<StreamGeometry, Message>(
                                () => PathGeometry.Parse(path.Data),
                                ImmutableList<IVDomNodeProperty<StreamGeometry, Message>>.Empty,
                                _ => Sub.None<Message>()))));
        }

        private static IEnumerable<IVDomNode<Message>> GetPlayerInfo(State state)
        {
            foreach (var player in state.Players)
            {
                var size = new Models.Size(30, 30);
                yield return VDomNode<DockPanel>()
                    .SetChildNodes(p => p.Children,
                        VDomNode<ContentControl>()
                            .Set(p => p.Width, size.Width)
                            .Set(p => p.Height, size.Height)
                            .Set(p => p.Content, GetPlayerView(player)
                                .Set(p => p.RenderTransform, VDomNode<ScaleTransform>()
                                    .Set(p => p.ScaleX, size.Width / player.Costume.Size.Width)
                                    .Set(p => p.ScaleY, size.Height / player.Costume.Size.Height)))
                            .Set(p => p.Margin, new Thickness(10)),
                        VDomNode<TextBlock>()
                            .Set(p => p.VerticalAlignment, VerticalAlignment.Center)
                            .Set(p => p.Margin, new Thickness(10))
                            .Set(p => p.Text, $"X: {player.Position.X:F2} | Y: {player.Position.Y:F2} | ∠ {player.Direction.Value:F2}°"));
            }
        }

        private static Sub<Message> Subscribe(State state)
        {
            return new Sub<Message>(
                "8994debe-794c-4e19-9276-abe669738280",
                (scheduler, dispatch) => dispatchSubject.Subscribe(p => dispatch(p)));
        }

        private class Proxy
        {
            private readonly Subject<Window> windowChanged = new Subject<Window>();
            private Window _window;

            public IObservable<Window> WindowChanged => windowChanged.AsObservable();

            public Window Window
            {
                get => _window;
                set
                {
                    _window = value;
                    windowChanged.OnNext(value);
                }
            }
        }

        internal static void DispatchMessageAndWaitForUpdate(Message message)
        {
            dispatchSubject.OnNext(message);
        }

        public static PlayerOnScene AddPlayer(Player player)
        {
            DispatchMessageAndWaitForUpdate(new Message.AddPlayer(player));
            return new PlayerOnScene(player.Id);
        }

        public static void ShowSceneAndAddTurtle()
        {
            ShowScene();
            Turtle.Default = AddPlayer(Turtle.DefaultPlayer);
        }

        public static void Sleep(double durationInMilliseconds)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(durationInMilliseconds));
        }

        public static void ClearScene()
        {
            DispatchMessageAndWaitForUpdate(new Message.ClearScene());
        }
    }
}