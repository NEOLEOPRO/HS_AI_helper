using System;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// «Стол устоялся»: подпись (стол, таверна, соперник) не менялась Seconds секунд. Тяжёлые расчёты (шансы до боя, сила стола, ход 1)
	/// запускаем только тогда — не на каждую покупку и перестановку (просьба: считать, когда стол ~2 с без изменений).
	/// </summary>
	internal class Settled
	{
		public const double Seconds = 2;

		private string _value;
		private DateTime _since;

		/// <summary>true — значение то же, что и Seconds секунд назад.</summary>
		public bool Check(string value)
		{
			if(value != _value)
			{
				_value = value;
				_since = DateTime.Now;
				return false;
			}
			return (DateTime.Now - _since).TotalSeconds >= Seconds;
		}

		public void Reset() => _value = null;
	}
}
