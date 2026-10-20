import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, sizes, tooltipPositions } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './TalearrAbsBadge.css';

// The header is rendered for the previous and next book too (swipe), so answers are shared
// for a short while instead of being fetched three times per page.
const cache = new Map();
const inflight = new Map();
const CACHE_MS = 30 * 1000;

function formatDuration(seconds) {
  if (!seconds) {
    return null;
  }

  const minutes = Math.round(seconds / 60);
  const hours = Math.floor(minutes / 60);

  return hours > 0 ? `${hours} h ${minutes % 60} min` : `${minutes} min`;
}

function describeMatch(item) {
  if (item.managedByTalearr) {
    return 'imported by Talearr';
  }

  switch (item.matchedBy) {
    case 'asin':
      return 'matched by ASIN';
    case 'isbn':
      return 'matched by ISBN';
    case 'title':
      return 'matched by title and author';
    default:
      return null;
  }
}

function describeItem(item) {
  const parts = [];

  if (item.hasAudio) {
    parts.push('audiobook');
  }

  if (item.hasEbook) {
    parts.push(item.ebookFormat ? `${item.ebookFormat} ebook` : 'ebook');
  }

  if (item.hasAudio && item.narrators) {
    parts.push(`read by ${item.narrators}`);
  }

  const duration = item.hasAudio ? formatDuration(item.durationSeconds) : null;

  if (duration) {
    parts.push(duration);
  }

  if (item.abridged !== 'unknown') {
    parts.push(item.abridged);
  }

  const match = describeMatch(item);

  if (match) {
    parts.push(match);
  }

  return parts.join(', ');
}

// Talearr: "In Audiobookshelf" on the book page (docs/TALEARR_AUDIOBOOKSHELF.md), linking to the
// Audiobookshelf item. Renders nothing when the book is not in Audiobookshelf or no
// Audiobookshelf connection syncs its library.
class TalearrAbsBadge extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      data: null
    };
  }

  componentDidMount() {
    this._isMounted = true;
    this.fetch();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.bookId !== this.props.bookId) {
      this.setState({ data: null });
      this.fetch();
    }
  }

  componentWillUnmount() {
    // Not aborted: the book page mounts the header more than once while it loads, and a
    // finished request fills the shared cache for the next mount.
    this._isMounted = false;
  }

  //
  // Control

  fetch() {
    const { bookId } = this.props;

    if (!bookId) {
      return;
    }

    const cached = cache.get(bookId);

    if (cached && Date.now() - cached.at < CACHE_MS) {
      this.setState({ data: cached.data });
      return;
    }

    const pending = inflight.get(bookId) || createAjaxRequest({
      url: `/talearr/audiobookshelf/book/${bookId}`
    }).request;

    inflight.set(bookId, pending);

    pending.done((data) => {
      cache.set(bookId, { at: Date.now(), data });

      if (this._isMounted && this.props.bookId === bookId) {
        this.setState({ data });
      }
    });

    pending.always(() => {
      inflight.delete(bookId);
    });

    // A failure (older Talearr, Chaptarr, no connection) shows nothing and is not cached.
  }

  //
  // Render

  render() {
    const {
      className,
      linkClassName,
      textClassName
    } = this.props;

    const { data } = this.state;

    if (!data || !data.inAudiobookshelf || !data.items || !data.items.length) {
      return null;
    }

    const items = data.items;
    const first = items.find((item) => item.url) || items[0];

    const label = (
      <Label
        className={className}
        kind={data.skipsGrabs ? kinds.SUCCESS : kinds.DEFAULT}
        size={sizes.LARGE}
      >
        <Icon
          name={icons.CHECK_CIRCLE}
          size={17}
        />

        <span className={textClassName || styles.text}>
          {'In Audiobookshelf'}
        </span>
      </Label>
    );

    const anchor = first.url ?
      (
        <Link
          className={linkClassName}
          to={first.url}
          title="Open in Audiobookshelf"
        >
          {label}
        </Link>
      ) :
      label;

    const tooltip = (
      <div className={styles.details}>
        {
          items.map((item) => {
            return (
              <div
                key={`${item.connectionId}:${item.itemId}`}
                className={styles.item}
              >
                <div className={styles.itemTitle}>
                  {item.title}{item.subtitle ? `: ${item.subtitle}` : ''}
                </div>

                <div>
                  {describeItem(item)}
                </div>

                <div className={styles.muted}>
                  {item.connectionName}
                </div>
              </div>
            );
          })
        }

        {
          data.skipsGrabs &&
            <div className={styles.muted}>
              {'Talearr does not grab this book while it is in Audiobookshelf.'}
            </div>
        }
      </div>
    );

    return (
      <Tooltip
        anchor={anchor}
        tooltip={tooltip}
        kind={kinds.INVERSE}
        position={tooltipPositions.BOTTOM}
      />
    );
  }
}

TalearrAbsBadge.propTypes = {
  bookId: PropTypes.number.isRequired,
  className: PropTypes.string,
  linkClassName: PropTypes.string,
  textClassName: PropTypes.string
};

export default TalearrAbsBadge;
