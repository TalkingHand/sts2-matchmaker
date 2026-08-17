using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Sts2Matchmaker.Helpers;

/// <summary>
/// Subscribes to game events whose delegate carries an argument type that differs between the general and
/// public-beta branches (e.g. StartRunLobby.PlayerConnected is Action&lt;LobbyPlayer&gt; on general but
/// Action&lt;StartRunLobbyPlayer&gt; on beta - same event name, incompatible delegate signature). A plain C#
/// "+=" subscription bakes the argument type into our assembly's metadata at compile time, which throws
/// ReflectionTypeLoadException the moment the game tries to load our DLL against whichever branch doesn't have
/// that exact type. This builds the delegate at runtime from whatever EventInfo.EventHandlerType actually is, so
/// our metadata never references the argument type at all - safe for callers who only care that the event fired,
/// not what it carries.
/// </summary>
public static class GameEventCompat
{
    public static Delegate Subscribe(object target, string eventName, Action callback)
    {
        EventInfo eventInfo = target.GetType().GetEvent(eventName)
            ?? throw new MissingMemberException(target.GetType().Name, eventName);
        Type handlerType = eventInfo.EventHandlerType!;
        ParameterInfo[] parameters = handlerType.GetMethod("Invoke")!.GetParameters();
        ParameterExpression[] parameterExpressions = Array.ConvertAll(parameters, p => Expression.Parameter(p.ParameterType));
        Expression body = Expression.Call(Expression.Constant(callback), typeof(Action).GetMethod(nameof(Action.Invoke))!);
        Delegate handler = Expression.Lambda(handlerType, body, parameterExpressions).Compile();
        eventInfo.AddEventHandler(target, handler);
        return handler;
    }

    public static void Unsubscribe(object target, string eventName, Delegate handler)
    {
        target.GetType().GetEvent(eventName)?.RemoveEventHandler(target, handler);
    }
}
