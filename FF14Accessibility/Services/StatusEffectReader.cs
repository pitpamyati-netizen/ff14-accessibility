using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace FF14Accessibility.Services;

public sealed class StatusEffectReader
{
	private readonly IPluginLog _log;

	public StatusEffectReader(IPluginLog log)
	{
		_log = log;
	}

	public List<string> Rows(IBattleChara chara, string logTag, bool withDescription)
	{
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		List<string> list = new List<string>();
		foreach (IStatus status in chara.StatusList)
		{
			if (status.StatusId == 0)
			{
				continue;
			}
			Status? valueNullable = status.GameData.ValueNullable;
			object obj;
			Status valueOrDefault;
			ReadOnlySeString val;
			if (!valueNullable.HasValue)
			{
				obj = null;
			}
			else
			{
				valueOrDefault = valueNullable.GetValueOrDefault();
				val = valueOrDefault.Name;
				obj = val.ExtractText().Trim();
			}
			if (obj == null)
			{
				obj = string.Empty;
			}
			string text = (string)obj;
			if (text.Length == 0)
			{
				continue;
			}
			string text2;
			if (!withDescription)
			{
				text2 = string.Empty;
			}
			else
			{
				object obj2;
				if (!valueNullable.HasValue)
				{
					obj2 = null;
				}
				else
				{
					valueOrDefault = valueNullable.GetValueOrDefault();
					val = valueOrDefault.Description;
					obj2 = val.ExtractText();
				}
				if (obj2 == null)
				{
					obj2 = string.Empty;
				}
				text2 = Flatten((string)obj2);
			}
			string text3 = text2;
			_log.Info($"[{logTag}] {status.StatusId}:'{text}' Param={status.Param} Rest={status.RemainingTime:0.0}s Desc='{text3}'", Array.Empty<object>());
			string text4 = text;
			int num = (int)status.RemainingTime;
			if (num > 0)
			{
				text4 += AccessibilityStrings.StatusEffectTimeLeft(num);
			}
			list.Add(AccessibilityStrings.StatusEffectDescription(text4, text3));
		}
		return list;
	}

	private static string Flatten(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		string text2 = text.Replace('\r', ' ').Replace('\n', ' ');
		while (text2.Contains("  ", StringComparison.Ordinal))
		{
			text2 = text2.Replace("  ", " ", StringComparison.Ordinal);
		}
		return text2.Trim();
	}
}
