import React from "react";

const preamble = "preamble-marker";

export const Card: React.FC<{ title: string }> = ({ title }) => {
    return <div className="card">{title}</div>;
};

export function ShallowButton(): JSX.Element {
    return <button>Click</button>;
}

export interface Item {
    id: number;
    label: string;
}

export function DeepItemList({ items }: { items: Item[] }): JSX.Element {
    return (
        <ul>
            {items.map((item) => (
                <li key={item.id}>{item.label}</li>
            ))}
        </ul>
    );
}

export class StatefulCounter extends React.Component<{ initial: number }, { count: number }> {
    constructor(props: { initial: number }) {
        super(props);
        this.state = { count: props.initial };
    }

    componentDidMount(): void {
        // mount side effect
    }

    render(): JSX.Element {
        return <button onClick={() => this.setState({ count: this.state.count + 1 })}>{this.state.count}</button>;
    }
}
